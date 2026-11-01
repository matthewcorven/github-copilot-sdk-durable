// Child ownership, fail-fast payment fan-out, and cancellation rollback.
// Run with the repository's TypeScript runner; DURABLE_RUNTIME_PATH selects replay.
import assert from "node:assert/strict";
import {
  DurableHarness,DurableStore,DurableTask,Extension,ExtensionRegistry,
  TaskCommit,TaskStatus,type PhaseHandler,
} from "../../lib/ts/durable.ts";

type PaymentInput={ card: string; answerAt: string };
type PaymentCheckpoint={ phase: string };
type CheckoutInput={ cards: string };
type CheckoutCheckpoint={ phase: string; payments: string[] };
const charged=new Set<string>();
const aborted=new Set<string>();

const payment=DurableTask.definePhases<PaymentInput,PaymentCheckpoint>(
  "example.payment",() => ({ phase: "charge" }),
  new Map<string,PhaseHandler<PaymentInput,PaymentCheckpoint>>([
    ["charge",async (input,_,runtime) => runtime.commitAsync(() => {
      if(!input.card.startsWith("expired")) charged.add(input.card);
      return TaskCommit.running({ phase: "confirm" });
    })],
    ["confirm",async (input,_,runtime) => runtime.sleepUntilAsync(
      new Date(Number(input.answerAt)),
      () => input.card.startsWith("expired")
        ? TaskCommit.failed(`${input.card} declined`)
        :TaskCommit.completed({ card: input.card }),
    )],
  ]),
  async (input,_,runtime) => runtime.commitAsync(() => {
    aborted.add(input.card);
    charged.delete(input.card);
    return TaskCommit.aborted();
  }),
);
const checkout=DurableTask.definePhases<CheckoutInput,CheckoutCheckpoint>(
  "example.checkout",() => ({ phase: "pay",payments: [] }),
  new Map<string,PhaseHandler<CheckoutInput,CheckoutCheckpoint>>([
    ["pay",async (input,_,runtime) => runtime.commitInTransactionAsync((tx) => {
      // Absolute deadlines preserve the reference ordering across scheduler delays.
      let answerAt=runtime.now.getTime()+1000;
      const payments: string[]=[];
      for(const card of input.cards.split(",")) {
        payments.push(runtime.createChildTask(
          tx,payment,{ card,answerAt: String(answerAt) },
        ).id);
        answerAt+=1000;
      }
      for(const id of payments) runtime.scheduleTask(id);
      return TaskCommit.waiting({ phase: "decide",payments },payments);
    })],
    ["decide",async (_,checkpoint,runtime) => runtime.commitInTransactionAsync(async () => {
      const outcomes=await runtime.outcomesAsync(checkpoint.payments);
      console.log(`  payments: ${outcomes.map((outcome) => outcome.status).join(", ")}`);
      return outcomes.every((outcome) => outcome.isCompleted)
        ? TaskCommit.completed("order placed"):TaskCommit.failed("payment failed");
    })],
  ]),
  async (_,__,runtime) => {
    console.log("  checkout aborted");
    return runtime.commitAsync(() => TaskCommit.aborted());
  },
);
const registry=new ExtensionRegistry();
registry.install(new Extension("checkout",[],[],[payment,checkout]));
async function main() {
  const store=DurableStore.createEphemeral();
  const harness=await DurableHarness.openAsync(store,registry);
  try {
    const root=await harness.rootAsync();
    const effective=await root.waitForEffectiveModelAsync();
    console.log(`effective model: ${effective.model??"(not reported)"}`);
    const runner=harness.runner;

    console.log("One card is declined:");
    const declined=await runner.createTaskAsync(
      checkout,{ cards: "visa-1,expired-2,visa-3,visa-4" },
      { ownerConversationId: root.id },
    );
    const declinedOutcome=await runner.waitForTaskAsync(declined.id);
    const declinedAborted=[...aborted].sort();
    console.log(`  checkout: ${declinedOutcome.status}`);
    console.log(`  aborted payments: ${declinedAborted.join(", ")}`);

    console.log("The customer cancels:");
    const cancelled=await runner.createTaskAsync(
      checkout,{ cards: "visa-5,visa-6,visa-7,visa-8" },
      { ownerConversationId: root.id },
    );
    const deadline=Date.now()+30_000;
    for(;;) {
      const children=store.tasks.filter((task) => task.ownerTaskId===cancelled.id);
      if(cancelled.status===TaskStatus.Waiting&&children.length===4&&
        children.every((task) => task.status===TaskStatus.Running&&
          task.checkpoint!==null&&typeof task.checkpoint==="object"&&
          !Array.isArray(task.checkpoint)&&task.checkpoint.phase==="confirm")) break;
      if(Date.now()>deadline) throw new Error("the payments never reached the bank");
      await new Promise((resolve) => setTimeout(resolve,5));
    }
    function printGraph() {
      const graph=runner.taskGraph();
      function printTask(node: (typeof graph)[number],depth: number) {
        const status=node.task.status===TaskStatus.Waiting
          ? `waiting on ${node.task.waitingOn.join(", ")}`
          :node.task.status.charAt(0).toUpperCase()+node.task.status.slice(1);
        console.log(`  ${"  ".repeat(depth)}${node.task.name} ${node.task.id}: ${status}`);
        for(const child of graph.filter((candidate) => candidate.task.ownerTaskId===node.task.id)) {
          printTask(child,depth+1);
        }
      }
      for(const node of graph.filter((candidate) => candidate.task.ownerTaskId===null)) {
        printTask(node,0);
      }
    }
    console.log("Task graph:");
    printGraph();
    await runner.abortTaskAsync(cancelled.id);
    const cancelledOutcome=await runner.waitForTaskAsync(cancelled.id);
    const cancelledAborted=[...aborted].sort();
    console.log(`  checkout: ${cancelledOutcome.status}`);
    console.log(`  aborted payments: ${cancelledAborted.join(", ")}`);
    console.log("Task graph after the abort:");
    printGraph();

    const graph=runner.taskGraph();
    const declinedChildren=graph.filter((node) => node.task.ownerTaskId===declined.id);
    assert.equal(declinedChildren.length,4,"the checkout owns four payment children");
    assert.ok(declinedChildren.every((node) => node.task.name==="example.payment"),"the children are payments");
    assert.ok(graph.some((node) => node.task.id===declined.id&&node.task.ownerConversationId===root.id),
      "the checkout is owned by the conversation");
    assert.equal(declinedOutcome.status,"failed","a declined payment fails the checkout");
    assert.ok(declinedOutcome.error!==null,"the failed checkout carries an error");
    assert.ok(!charged.has("expired-2"),"the declined card was never charged");
    assert.ok(charged.has("visa-1"),"the other cards were charged");
    assert.deepEqual(declinedAborted,["visa-3","visa-4"],"the declined card aborts the siblings still waiting at the bank");
    assert.equal(cancelledOutcome.status,"aborted","the cancelled checkout is aborted");
    assert.ok(!charged.has("visa-5"),"an aborted checkout undoes its effects");
    assert.ok(!charged.has("visa-6"),"every aborted payment undoes its effect");
    const children=graph.filter((node) => node.task.ownerTaskId===cancelled.id);
    assert.equal(children.length,4,"the cancelled checkout still owns its four children");
    assert.ok(children.every((node) => node.task.status===TaskStatus.Terminal),
      "every child of an aborted checkout is terminal");
    assert.ok(children.every((node) => node.task.outcome!==null&&
      typeof node.task.outcome==="object"&&!Array.isArray(node.task.outcome)&&
      node.task.outcome.status==="aborted"),
      "every child of an aborted checkout is aborted, not completed");
    assert.deepEqual(cancelledAborted,["visa-3","visa-4","visa-5","visa-6","visa-7","visa-8"],
      "both fan-outs aborted every payment they reached");
  } finally {
    await harness.disposeAsync();
    store.dispose();
  }
}

main().catch((error: unknown) => {
  console.error(error);
  process.exitCode=1;
});
