// Mirrors every store-only assertion in examples/32-jsonl-store.cs; no SDK session.
import assert from "node:assert/strict";
import { mkdtempSync,existsSync,readFileSync,appendFileSync,rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename,dirname,isAbsolute,join } from "node:path";
import {
  DocumentDefinition,DurableStore,DurableStoreOptions,
} from "../../lib/ts/durable.ts";

async function main() {
  const directory=mkdtempSync(join(tmpdir(),"durable-example-"));
  const storePath=join(directory,"state.jsonl");
  const notes=new DocumentDefinition<{ text: string }>("example.notes");
  function openStore() {
    const options=new DurableStoreOptions();
    options.path=storePath;
    return DurableStore.create(options);
  }
  try {
    const store=openStore();
    try {
      assert.ok(store.isPersistent,"an explicit path makes the store persistent");
      await store.commitAsync((tx) => {
        const conversation=tx.createConversation();
        const draft=tx.doc(notes,conversation.id);
        draft.value={ text: "written before the restart" };
        draft.save(tx.appendEntry(conversation.id,"notes.write"));
      });
    } finally {
      store.dispose();
    }
    assert.ok(existsSync(storePath),"the store file exists after the commit");
    const lines=readFileSync(storePath,"utf8").split("\n").filter(Boolean);
    assert.ok(lines.length>1,"the store writes one record per line");
    assert.ok(lines.every((line) => line.startsWith("{")&&line.includes('"type"')&&
      typeof JSON.parse(line)==="object"),"every line is a JSONL record envelope");

    const reopened=openStore();
    try {
      assert.equal(reopened.documents.length,1,"the document was read back");
      const value=reopened.documents[0]?.latest;
      assert.ok(value!==null&&typeof value==="object"&&!Array.isArray(value),
        "the reopened document holds an object");
      assert.equal(value.text,"written before the restart",
        "the reopened store holds the value the first store committed");
    } finally {
      reopened.dispose();
    }
    appendFileSync(storePath,'{"v":1,"seq":9999,"ty');
    const recovered=openStore();
    try {
      assert.equal(recovered.documents.length,1,"the committed document survived");
      assert.equal(recovered.documents[0]?.versions.length,1,
        "the committed record survived the torn tail");
    } finally {
      recovered.dispose();
    }
    assert.ok(!readFileSync(storePath,"utf8").includes('"seq":9999'),
      "the torn tail was truncated away at open");

    // Resolve only: writing the default would leave state in the user's home.
    const defaultPath=DurableStore.defaultStorePath();
    assert.equal(basename(defaultPath),"durable-store.jsonl","the default file name is stable");
    assert.equal(basename(dirname(defaultPath)),"durable","the default file sits in a durable directory");
    assert.ok(isAbsolute(defaultPath),"the default path is absolute, so it does not depend on the working directory");
    const ephemeral=DurableStore.createEphemeral();
    try {
      assert.ok(!ephemeral.isPersistent,"an ephemeral store persists nothing");
    } finally {
      ephemeral.dispose();
    }
    console.log(`explicit store file: ${storePath}`);
    console.log(`default store file:  ${defaultPath}`);
    console.log(`tiers: durable writes ${lines.length} JSONL record(s); ephemeral writes none`);
  } finally {
    rmSync(directory,{ recursive: true });
  }
}

main().catch((error: unknown) => {
  console.error(error);
  process.exitCode=1;
});
