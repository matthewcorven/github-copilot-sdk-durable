package dev.durable.bande.foundry

import android.content.Context
import com.microsoft.foundrylocal.api.ChatClient
import com.microsoft.foundrylocal.api.Configuration
import com.microsoft.foundrylocal.api.FoundryLocalManager
import com.microsoft.foundrylocal.api.Model
import com.microsoft.foundrylocal.api.ChatCompletionRequest
import com.microsoft.foundrylocal.api.ChatMessage
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch

/**
 * The Kotlin facade from DECISION-bridge.md §3.1.
 *
 * It exists so that no suspend function, `Flow` or SDK datamodel crosses into .NET: the public
 * surface here is `String`, `Long`, `Int` and [Listener]. Coroutines stay on this side, in a
 * process-level scope owned by the bridge — the exception `docs/BEST_PRACTICES.md` names for a
 * component that intentionally outlives an Activity.
 *
 * The .NET side drives the sequence [start] -> [ensureModel] -> [complete] (or
 * [completeStreaming]). History is passed in on every call because the SDK does not manage it.
 */
class FoundryBridge(context: Context, appName: String) {

    /**
     * Results are delivered here, always on the bridge's IO dispatcher. Implementations must
     * not block, and must marshal to their own thread before touching UI.
     */
    interface Listener {
        fun onChunk(text: String)
        fun onCompleted(text: String)
        fun onFailed(code: Int, message: String, disconnected: Boolean)
        fun onDisconnected()
    }

    private val appContext: Context = context.applicationContext
    private val appName: String = appName

    // Process-level scope, intentionally outliving any Activity.
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private var manager: FoundryLocalManager? = null
    private var model: Model? = null
    private var chatClient: ChatClient? = null
    private var activeJob: Job? = null
    private val turnInFlight = AtomicBoolean(false)

    /** Creates the manager and checks service-app compatibility. Idempotent. */
    fun start(listener: Listener) {
        scope.launch {
            if (manager != null) {
                listener.onCompleted("already started")
                return@launch
            }
            try {
                val created = FoundryLocalManager.create(
                    appContext,
                    Configuration(appName = appName),
                )

                // The AAR cannot pin the service-app version, so the guide's answer is used:
                // check compatibility rather than assume it (decision record §2).
                val compat = created.checkCompatibility()
                if (!compat.isCompatible) {
                    listener.onFailed(
                        CODE_INCOMPATIBLE,
                        "service app incompatible: ${compat.message} " +
                            "(sdk ${compat.sdkVersion}, app ${compat.appVersion})",
                        false,
                    )
                    return@launch
                }

                manager = created
                if (!created.isConnected) {
                    listener.onFailed(CODE_DISCONNECTED, "manager created but not connected", true)
                    return@launch
                }
                listener.onCompleted("started")
            } catch (t: Throwable) {
                listener.onFailed(CODE_START_FAILED, t.describe(), true)
            }
        }
    }

    /**
     * Reports the catalog's aliases as a comma-separated string on [Listener.onCompleted], or a
     * classified failure. Exists because the model alias is catalog-specific and the AAR does not
     * pin one: guessing an alias would turn "wrong name" into an unexplained failure.
     */
    fun listModelAliases(listener: Listener) {
        scope.launch {
            val m = manager
            if (m == null) {
                listener.onFailed(CODE_NOT_STARTED, "start() first", false)
                return@launch
            }
            try {
                val models = m.getCatalog().listModels()
                if (models.isEmpty()) {
                    listener.onFailed(CODE_MODEL_MISSING, "catalog is empty", false)
                    return@launch
                }
                listener.onCompleted(models.joinToString(", ") { it.alias })
            } catch (t: Throwable) {
                listener.onFailed(CODE_MODEL_FAILED, t.describe(), true)
            }
        }
    }

    /** Resolves the alias, downloads it if it is not cached, loads it, and creates a chat client. */
    fun ensureModel(alias: String, listener: Listener) {
        scope.launch {
            val m = manager
            if (m == null) {
                listener.onFailed(CODE_NOT_STARTED, "start() first", false)
                return@launch
            }
            try {
                val resolved = m.getCatalog().getModel(alias)
                if (resolved == null) {
                    listener.onFailed(CODE_MODEL_MISSING, "alias '$alias' not in catalog", false)
                    return@launch
                }

                // The service can report Failure from download_model even when it has nothing
                // to fetch: its own log shows "Blob enumeration complete: 0 to download,
                // 6 skipped" and 2.7 GB already on disk, then Status:Failure. Neither the
                // download status nor isCached() is trustworthy in that state, so the download
                // is attempted best-effort and load() is left to judge whether the model is
                // actually usable.
                var downloadFailure: Throwable? = null
                if (!resolved.isCached()) {
                    listener.onChunk("ensuring '$alias'")
                    try {
                        resolved.download(progress = { progress: Float ->
                            listener.onChunk("download ${(progress * 100).toInt()}%")
                        })
                    } catch (t: Throwable) {
                        downloadFailure = t
                        listener.onChunk("download reported: ${t.describe()}")
                    }
                }

                try {
                    resolved.load()
                } catch (t: Throwable) {
                    // Load failed. If the download was the first thing to fail, report that,
                    // because it is the actionable cause and it came first.
                    throw downloadFailure ?: t
                }
                chatClient = resolved.createChatClient()
                model = resolved
                listener.onCompleted("model ready: $alias")
            } catch (t: Throwable) {
                listener.onFailed(CODE_MODEL_FAILED, t.describe(), true)
            }
        }
    }

    /** One non-streaming turn. Returns immediately; the answer arrives on [listener]. */
    fun complete(history: Array<String>, requestId: Long, listener: Listener) {
        runTurn(history, requestId, listener, streaming = false)
    }

    /** One streaming turn: `onChunk` per delta, then `onCompleted` with the assembled text. */
    fun completeStreaming(history: Array<String>, requestId: Long, listener: Listener) {
        runTurn(history, requestId, listener, streaming = true)
    }

    private fun runTurn(
        history: Array<String>,
        requestId: Long,
        listener: Listener,
        streaming: Boolean,
    ) {
        // One turn per conversation keeps transcript order deterministic (§3.1 property 2).
        if (!turnInFlight.compareAndSet(false, true)) {
            listener.onFailed(CODE_BUSY, "a turn is already in flight", true)
            return
        }

        val m = manager
        val client = chatClient
        if (m == null || client == null) {
            turnInFlight.set(false)
            listener.onFailed(CODE_NOT_READY, "start() and ensureModel() first", false)
            return
        }

        activeJob = scope.launch {
            try {
                val request = ChatCompletionRequest(messages = history.map(::parseMessage))

                if (streaming) {
                    val assembled = StringBuilder()
                    client.completeChatStreaming(request).collect { chunk ->
                        val delta = chunk.delta ?: ""
                        if (delta.isNotEmpty()) {
                            assembled.append(delta)
                            listener.onChunk(delta)
                        }
                    }
                    listener.onCompleted(assembled.toString())
                } else {
                    val completion = client.completeChat(request)
                    listener.onCompleted(completion.message?.content ?: "")
                }
            } catch (c: CancellationException) {
                listener.onFailed(CODE_CANCELLED, "cancelled", false)
            } catch (t: Throwable) {
                // A disconnect mid-turn is the distinct signal variant 2 depends on (§4).
                if (!m.isConnected) {
                    listener.onDisconnected()
                } else {
                    listener.onFailed(CODE_TURN_FAILED, t.describe(), true)
                }
            } finally {
                turnInFlight.set(false)
            }
        }
    }

    /**
     * Re-establishes the service connection after a disconnect. Pre-disconnect handles are
     * stale, so the chat client is dropped and must be reacquired via [ensureModel].
     */
    fun reconnect(listener: Listener) {
        scope.launch {
            val m = manager ?: return@launch listener.onFailed(CODE_NOT_STARTED, "start() first", false)
            try {
                m.reconnect()
                chatClient = null
                model = null
                listener.onCompleted("reconnected")
            } catch (t: Throwable) {
                listener.onFailed(CODE_START_FAILED, t.describe(), true)
            }
        }
    }

    /** Cancels the in-flight turn — the SDK's documented stop mechanism. */
    fun cancel(requestId: Long) {
        activeJob?.cancel()
        activeJob = null
    }

    fun close() {
        try {
            activeJob?.cancel()
            chatClient = null
            model = null
            manager?.close()
        } finally {
            manager = null
            scope.coroutineContext[Job]?.cancel()
        }
    }

    private fun parseMessage(encoded: String): ChatMessage {
        // .NET sends "role\u0001content" so history crosses the boundary as plain strings.
        val split = encoded.indexOf('\u0001')
        return if (split < 0) {
            ChatMessage(ChatMessage.ROLE_USER, encoded)
        } else {
            ChatMessage(encoded.substring(0, split), encoded.substring(split + 1))
        }
    }

    private fun Throwable.describe(): String =
        "${this::class.java.simpleName}: ${message ?: "(no message)"}"

    companion object {
        const val CODE_START_FAILED = 1
        const val CODE_INCOMPATIBLE = 2
        const val CODE_DISCONNECTED = 3
        const val CODE_MODEL_MISSING = 4
        const val CODE_MODEL_FAILED = 5
        const val CODE_NOT_READY = 6
        const val CODE_BUSY = 7
        const val CODE_CANCELLED = 8
        const val CODE_NOT_STARTED = 9
        const val CODE_TURN_FAILED = 10
    }
}
