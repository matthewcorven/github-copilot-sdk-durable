import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * Attaches to the deterministic replay runtime over stdio and completes one recorded turn.
 *
 * <p>Standard library only: the check must run with a bare JDK and no network access, so the
 * small JSON reader below stands in for a JSON library.
 *
 * <p>Usage: {@code java Attach <runtime-command> [runtime-args...]}. The runtime command is
 * supplied by run-all.sh so this check never guesses how the runtime is launched.
 */
public final class Attach {

    private static final String SESSION_ID = "session-replay-1";
    private static final String EXPECTED_MESSAGE_ID = "msg-0001";
    private static final String EXPECTED_CONTENT = "Hello from the recorded transcript.";

    private Attach() {
    }

    // -----------------------------------------------------------------------
    // Minimal JSON value and parser
    // -----------------------------------------------------------------------

    /** A parsed JSON value: {@code null}, {@link Boolean}, {@link Double}, {@link String}, {@link List} or {@link Map}. */
    static final class Json {

        private Json() {
        }

        static Object parse(String text) {
            Parser parser = new Parser(text);
            Object value = parser.value();
            parser.skipWhitespace();
            if (parser.pos != text.length()) {
                throw new IllegalArgumentException("trailing input at index " + parser.pos);
            }
            return value;
        }

        @SuppressWarnings("unchecked")
        static Map<String, Object> asObject(Object value) {
            return (Map<String, Object>) value;
        }

        @SuppressWarnings("unchecked")
        static List<Object> asArray(Object value) {
            return (List<Object>) value;
        }

        static String asString(Object value) {
            return (String) value;
        }

        static String string(Map<String, Object> object, String key) {
            Object value = object.get(key);
            return value instanceof String ? (String) value : null;
        }

        static String toJson(Object value) {
            StringBuilder out = new StringBuilder();
            write(value, out);
            return out.toString();
        }

        private static void write(Object value, StringBuilder out) {
            if (value == null) {
                out.append("null");
            } else if (value instanceof String) {
                quote((String) value, out);
            } else if (value instanceof Boolean || value instanceof Integer || value instanceof Long) {
                out.append(value);
            } else if (value instanceof Double) {
                double number = (Double) value;
                if (number == Math.rint(number) && !Double.isInfinite(number)) {
                    out.append((long) number);
                } else {
                    out.append(number);
                }
            } else if (value instanceof Map) {
                out.append('{');
                boolean first = true;
                for (Map.Entry<?, ?> entry : ((Map<?, ?>) value).entrySet()) {
                    if (!first) {
                        out.append(',');
                    }
                    first = false;
                    quote(String.valueOf(entry.getKey()), out);
                    out.append(':');
                    write(entry.getValue(), out);
                }
                out.append('}');
            } else if (value instanceof List) {
                out.append('[');
                boolean first = true;
                for (Object item : (List<?>) value) {
                    if (!first) {
                        out.append(',');
                    }
                    first = false;
                    write(item, out);
                }
                out.append(']');
            } else {
                throw new IllegalArgumentException("cannot serialize " + value.getClass());
            }
        }

        private static void quote(String value, StringBuilder out) {
            out.append('"');
            for (int i = 0; i < value.length(); i++) {
                char ch = value.charAt(i);
                switch (ch) {
                    case '"' -> out.append("\\\"");
                    case '\\' -> out.append("\\\\");
                    case '\n' -> out.append("\\n");
                    case '\r' -> out.append("\\r");
                    case '\t' -> out.append("\\t");
                    default -> {
                        if (ch < 0x20) {
                            out.append(String.format("\\u%04x", (int) ch));
                        } else {
                            out.append(ch);
                        }
                    }
                }
            }
            out.append('"');
        }
    }

    private static final class Parser {

        private final String text;
        private int pos;

        Parser(String text) {
            this.text = text;
        }

        void skipWhitespace() {
            while (pos < text.length() && Character.isWhitespace(text.charAt(pos))) {
                pos++;
            }
        }

        private char peek() {
            if (pos >= text.length()) {
                throw new IllegalArgumentException("unexpected end of input");
            }
            return text.charAt(pos);
        }

        private void expect(char expected) {
            if (peek() != expected) {
                throw new IllegalArgumentException("expected '" + expected + "' at index " + pos);
            }
            pos++;
        }

        Object value() {
            skipWhitespace();
            char ch = peek();
            return switch (ch) {
                case '{' -> object();
                case '[' -> array();
                case '"' -> string();
                case 't' -> literal("true", Boolean.TRUE);
                case 'f' -> literal("false", Boolean.FALSE);
                case 'n' -> literal("null", null);
                default -> number();
            };
        }

        private Object literal(String literal, Object value) {
            if (!text.startsWith(literal, pos)) {
                throw new IllegalArgumentException("invalid literal at index " + pos);
            }
            pos += literal.length();
            return value;
        }

        private Object number() {
            int start = pos;
            while (pos < text.length() && "+-.eE0123456789".indexOf(text.charAt(pos)) >= 0) {
                pos++;
            }
            return Double.parseDouble(text.substring(start, pos));
        }

        private String string() {
            expect('"');
            StringBuilder out = new StringBuilder();
            while (true) {
                char ch = peek();
                pos++;
                if (ch == '"') {
                    return out.toString();
                }
                if (ch != '\\') {
                    out.append(ch);
                    continue;
                }
                char escape = peek();
                pos++;
                switch (escape) {
                    case '"' -> out.append('"');
                    case '\\' -> out.append('\\');
                    case '/' -> out.append('/');
                    case 'b' -> out.append('\b');
                    case 'f' -> out.append('\f');
                    case 'n' -> out.append('\n');
                    case 'r' -> out.append('\r');
                    case 't' -> out.append('\t');
                    case 'u' -> {
                        out.append((char) Integer.parseInt(text.substring(pos, pos + 4), 16));
                        pos += 4;
                    }
                    default -> throw new IllegalArgumentException("invalid escape '\\" + escape + "'");
                }
            }
        }

        private List<Object> array() {
            expect('[');
            List<Object> items = new ArrayList<>();
            skipWhitespace();
            if (peek() == ']') {
                pos++;
                return items;
            }
            while (true) {
                items.add(value());
                skipWhitespace();
                char ch = peek();
                pos++;
                if (ch == ']') {
                    return items;
                }
                if (ch != ',') {
                    throw new IllegalArgumentException("expected ',' or ']' at index " + (pos - 1));
                }
            }
        }

        private Map<String, Object> object() {
            expect('{');
            Map<String, Object> map = new LinkedHashMap<>();
            skipWhitespace();
            if (peek() == '}') {
                pos++;
                return map;
            }
            while (true) {
                skipWhitespace();
                String key = string();
                skipWhitespace();
                expect(':');
                map.put(key, value());
                skipWhitespace();
                char ch = peek();
                pos++;
                if (ch == '}') {
                    return map;
                }
                if (ch != ',') {
                    throw new IllegalArgumentException("expected ',' or '}' at index " + (pos - 1));
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // JSON-RPC client over the runtime's LSP-style framing
    // -----------------------------------------------------------------------

    private static final class Runtime implements AutoCloseable {

        private final Process process;
        private final OutputStream stdin;
        private final InputStream stdout;
        private final List<Map<String, Object>> events = new ArrayList<>();
        private int nextId = 1;

        Runtime(List<String> argv) throws IOException {
            ProcessBuilder builder = new ProcessBuilder(argv);
            builder.redirectError(ProcessBuilder.Redirect.INHERIT);
            this.process = builder.start();
            this.stdin = process.getOutputStream();
            this.stdout = process.getInputStream();
        }

        /**
         * Writes one LSP-style frame. The runtime reads nothing without the Content-Length
         * header, so every write goes through this framing.
         */
        private void writeFrame(Object message) throws IOException {
            byte[] body = Json.toJson(message).getBytes(StandardCharsets.UTF_8);
            stdin.write(("Content-Length: " + body.length + "\r\n\r\n").getBytes(StandardCharsets.US_ASCII));
            stdin.write(body);
            stdin.flush();
        }

        private String readLine() throws IOException {
            ByteArrayOutputStream line = new ByteArrayOutputStream();
            while (true) {
                int read = stdout.read();
                if (read < 0) {
                    return line.size() == 0 ? null : line.toString(StandardCharsets.UTF_8);
                }
                if (read == '\n') {
                    return line.toString(StandardCharsets.UTF_8);
                }
                if (read != '\r') {
                    line.write(read);
                }
            }
        }

        private Map<String, Object> readFrame() throws IOException {
            Integer contentLength = null;
            while (true) {
                String line = readLine();
                if (line == null) {
                    throw new IOException("runtime closed the stream while reading headers");
                }
                if (line.isEmpty()) {
                    break;
                }
                int colon = line.indexOf(':');
                if (colon > 0 && line.substring(0, colon).trim().equalsIgnoreCase("content-length")) {
                    contentLength = Integer.parseInt(line.substring(colon + 1).trim());
                }
            }

            if (contentLength == null) {
                throw new IOException("frame is missing a Content-Length header");
            }

            byte[] body = stdout.readNBytes(contentLength);
            if (body.length != contentLength) {
                throw new IOException("runtime closed the stream while reading the body");
            }
            return Json.asObject(Json.parse(new String(body, StandardCharsets.UTF_8)));
        }

        Map<String, Object> request(String method, Map<String, Object> params) throws IOException {
            int id = nextId++;
            Map<String, Object> message = new LinkedHashMap<>();
            message.put("jsonrpc", "2.0");
            message.put("id", id);
            message.put("method", method);
            message.put("params", params);
            writeFrame(message);

            while (true) {
                Map<String, Object> response = readFrame();

                if ("session.event".equals(Json.string(response, "method"))) {
                    Map<String, Object> envelope = Json.asObject(response.get("params"));
                    events.add(Json.asObject(envelope.get("event")));
                    continue;
                }

                Object responseId = response.get("id");
                if (!(responseId instanceof Double) || ((Double) responseId).intValue() != id) {
                    continue;
                }

                if (response.get("error") != null) {
                    Map<String, Object> error = Json.asObject(response.get("error"));
                    throw new IOException(error.get("code") + ": " + Json.string(error, "message"));
                }

                return Json.asObject(response.get("result"));
            }
        }

        Map<String, Object> assistantMessage() {
            for (Map<String, Object> event : events) {
                if ("assistant.message".equals(Json.string(event, "type"))) {
                    return event;
                }
            }
            return null;
        }

        @Override
        public void close() {
            try {
                stdin.close();
            } catch (IOException ignored) {
                // The runtime may already have exited.
            }
            try {
                process.waitFor();
            } catch (InterruptedException interrupted) {
                Thread.currentThread().interrupt();
            }
        }
    }

    private static Map<String, Object> object(Object... keyValues) {
        Map<String, Object> map = new LinkedHashMap<>();
        for (int i = 0; i < keyValues.length; i += 2) {
            map.put((String) keyValues[i], keyValues[i + 1]);
        }
        return map;
    }

    private static void run(List<String> argv) throws IOException {
        try (Runtime runtime = new Runtime(argv)) {
            Map<String, Object> connect = runtime.request("connect", object());
            if (!Boolean.TRUE.equals(connect.get("ok"))) {
                throw new IOException("connect did not report ok: " + Json.toJson(connect));
            }

            Map<String, Object> created =
                    runtime.request("session.create", object("sessionId", SESSION_ID));
            if (!SESSION_ID.equals(Json.string(created, "sessionId"))) {
                throw new IOException("session.create returned " + Json.toJson(created));
            }

            Map<String, Object> sent = runtime.request(
                    "session.sendMessages",
                    object(
                            "sessionId", SESSION_ID,
                            "messages", List.of(object("role", "user", "content", "Hello"))));
            List<Object> messageIds = Json.asArray(sent.get("messageIds"));
            if (messageIds.size() != 1 || !EXPECTED_MESSAGE_ID.equals(messageIds.get(0))) {
                throw new IOException(
                        "session.sendMessages returned " + Json.toJson(sent)
                                + ", expected ['" + EXPECTED_MESSAGE_ID + "']");
            }

            Map<String, Object> message = runtime.assistantMessage();
            if (message == null) {
                throw new IOException("no assistant.message event was emitted");
            }
            String content = Json.string(Json.asObject(message.get("data")), "content");
            if (!EXPECTED_CONTENT.equals(content)) {
                throw new IOException("assistant.message content was '" + content + "'");
            }

            // getMessages must replay the same transcript the events carried.
            Map<String, Object> transcript =
                    runtime.request("session.getMessages", object("sessionId", SESSION_ID));
            boolean replayed = false;
            for (Object item : Json.asArray(transcript.get("events"))) {
                Map<String, Object> event = Json.asObject(item);
                if ("assistant.message".equals(Json.string(event, "type"))
                        && EXPECTED_CONTENT.equals(
                                Json.string(Json.asObject(event.get("data")), "content"))) {
                    replayed = true;
                    break;
                }
            }
            if (!replayed) {
                throw new IOException("session.getMessages did not replay the recorded assistant message");
            }

            runtime.request("runtime.shutdown", object());
        }

        System.out.println("java: PASS (" + EXPECTED_MESSAGE_ID + ": " + EXPECTED_CONTENT + ")");
    }

    public static void main(String[] args) {
        if (args.length == 0) {
            System.err.println("usage: java Attach <runtime-command> [runtime-args...]");
            System.exit(2);
        }

        try {
            run(List.of(args));
        } catch (IOException | RuntimeException error) {
            System.err.println("java: " + error.getMessage());
            System.exit(1);
        }
    }
}
