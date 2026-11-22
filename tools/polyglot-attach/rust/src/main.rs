//! Attaches to the deterministic replay runtime over stdio and completes one
//! recorded turn.
//!
//! Standard library only: the check must run with a bare Rust toolchain and no
//! network access, so the small JSON reader below stands in for a crate.

use std::collections::BTreeMap;
use std::io::{BufRead, BufReader, Read, Write};
use std::process::{Child, ChildStdin, ChildStdout, Command, Stdio};

const SESSION_ID: &str = "session-replay-1";
const EXPECTED_MESSAGE_ID: &str = "msg-0001";
const EXPECTED_CONTENT: &str = "Hello from the recorded transcript.";

// ---------------------------------------------------------------------------
// Minimal JSON value and parser
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, PartialEq)]
enum Json {
    Null,
    Bool(bool),
    Number(f64),
    String(String),
    Array(Vec<Json>),
    Object(BTreeMap<String, Json>),
}

impl Json {
    fn get(&self, key: &str) -> Option<&Json> {
        match self {
            Json::Object(map) => map.get(key),
            _ => None,
        }
    }

    fn as_str(&self) -> Option<&str> {
        match self {
            Json::String(value) => Some(value),
            _ => None,
        }
    }

    fn as_array(&self) -> Option<&[Json]> {
        match self {
            Json::Array(items) => Some(items),
            _ => None,
        }
    }

    fn as_bool(&self) -> Option<bool> {
        match self {
            Json::Bool(value) => Some(*value),
            _ => None,
        }
    }

    fn as_i64(&self) -> Option<i64> {
        match self {
            Json::Number(value) => Some(*value as i64),
            _ => None,
        }
    }

    fn to_json(&self) -> String {
        match self {
            Json::Null => "null".to_string(),
            Json::Bool(value) => value.to_string(),
            Json::Number(value) => {
                if value.fract() == 0.0 {
                    format!("{}", *value as i64)
                } else {
                    value.to_string()
                }
            }
            Json::String(value) => quote(value),
            Json::Array(items) => {
                let parts: Vec<String> = items.iter().map(Json::to_json).collect();
                format!("[{}]", parts.join(","))
            }
            Json::Object(map) => {
                let parts: Vec<String> = map
                    .iter()
                    .map(|(key, value)| format!("{}:{}", quote(key), value.to_json()))
                    .collect();
                format!("{{{}}}", parts.join(","))
            }
        }
    }
}

fn quote(value: &str) -> String {
    let mut out = String::with_capacity(value.len() + 2);
    out.push('"');
    for ch in value.chars() {
        match ch {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)),
            c => out.push(c),
        }
    }
    out.push('"');
    out
}

struct Parser<'a> {
    bytes: &'a [u8],
    pos: usize,
}

impl<'a> Parser<'a> {
    fn new(text: &'a str) -> Self {
        Parser { bytes: text.as_bytes(), pos: 0 }
    }

    fn parse(text: &'a str) -> Result<Json, String> {
        let mut parser = Parser::new(text);
        let value = parser.value()?;
        parser.skip_whitespace();
        if parser.pos != parser.bytes.len() {
            return Err(format!("trailing input at byte {}", parser.pos));
        }
        Ok(value)
    }

    fn skip_whitespace(&mut self) {
        while self.pos < self.bytes.len() && self.bytes[self.pos].is_ascii_whitespace() {
            self.pos += 1;
        }
    }

    fn peek(&self) -> Option<u8> {
        self.bytes.get(self.pos).copied()
    }

    fn expect(&mut self, byte: u8) -> Result<(), String> {
        if self.peek() == Some(byte) {
            self.pos += 1;
            Ok(())
        } else {
            Err(format!("expected '{}' at byte {}", byte as char, self.pos))
        }
    }

    fn value(&mut self) -> Result<Json, String> {
        self.skip_whitespace();
        match self.peek() {
            Some(b'{') => self.object(),
            Some(b'[') => self.array(),
            Some(b'"') => self.string().map(Json::String),
            Some(b't') => self.literal("true", Json::Bool(true)),
            Some(b'f') => self.literal("false", Json::Bool(false)),
            Some(b'n') => self.literal("null", Json::Null),
            Some(_) => self.number(),
            None => Err("unexpected end of input".to_string()),
        }
    }

    fn literal(&mut self, text: &str, value: Json) -> Result<Json, String> {
        if self.bytes[self.pos..].starts_with(text.as_bytes()) {
            self.pos += text.len();
            Ok(value)
        } else {
            Err(format!("invalid literal at byte {}", self.pos))
        }
    }

    fn number(&mut self) -> Result<Json, String> {
        let start = self.pos;
        while let Some(byte) = self.peek() {
            if byte.is_ascii_digit() || matches!(byte, b'-' | b'+' | b'.' | b'e' | b'E') {
                self.pos += 1;
            } else {
                break;
            }
        }
        let text = std::str::from_utf8(&self.bytes[start..self.pos])
            .map_err(|_| "invalid number".to_string())?;
        text.parse::<f64>()
            .map(Json::Number)
            .map_err(|_| format!("invalid number '{}'", text))
    }

    fn string(&mut self) -> Result<String, String> {
        self.expect(b'"')?;
        let mut out = String::new();
        loop {
            let byte = self.peek().ok_or_else(|| "unterminated string".to_string())?;
            self.pos += 1;
            match byte {
                b'"' => return Ok(out),
                b'\\' => {
                    let escape = self.peek().ok_or_else(|| "unterminated escape".to_string())?;
                    self.pos += 1;
                    match escape {
                        b'"' => out.push('"'),
                        b'\\' => out.push('\\'),
                        b'/' => out.push('/'),
                        b'b' => out.push('\u{0008}'),
                        b'f' => out.push('\u{000c}'),
                        b'n' => out.push('\n'),
                        b'r' => out.push('\r'),
                        b't' => out.push('\t'),
                        b'u' => {
                            let hex = self
                                .bytes
                                .get(self.pos..self.pos + 4)
                                .ok_or_else(|| "truncated \\u escape".to_string())?;
                            let hex = std::str::from_utf8(hex)
                                .map_err(|_| "invalid \\u escape".to_string())?;
                            let code = u32::from_str_radix(hex, 16)
                                .map_err(|_| "invalid \\u escape".to_string())?;
                            self.pos += 4;
                            out.push(char::from_u32(code).unwrap_or('\u{fffd}'));
                        }
                        other => return Err(format!("invalid escape '\\{}'", other as char)),
                    }
                }
                _ => {
                    // Re-decode the UTF-8 sequence this byte belongs to.
                    let start = self.pos - 1;
                    let width = utf8_width(byte);
                    let end = (start + width).min(self.bytes.len());
                    let text = std::str::from_utf8(&self.bytes[start..end])
                        .map_err(|_| "invalid UTF-8 in string".to_string())?;
                    out.push_str(text);
                    self.pos = end;
                }
            }
        }
    }

    fn array(&mut self) -> Result<Json, String> {
        self.expect(b'[')?;
        let mut items = Vec::new();
        self.skip_whitespace();
        if self.peek() == Some(b']') {
            self.pos += 1;
            return Ok(Json::Array(items));
        }
        loop {
            items.push(self.value()?);
            self.skip_whitespace();
            match self.peek() {
                Some(b',') => {
                    self.pos += 1;
                }
                Some(b']') => {
                    self.pos += 1;
                    return Ok(Json::Array(items));
                }
                _ => return Err(format!("expected ',' or ']' at byte {}", self.pos)),
            }
        }
    }

    fn object(&mut self) -> Result<Json, String> {
        self.expect(b'{')?;
        let mut map = BTreeMap::new();
        self.skip_whitespace();
        if self.peek() == Some(b'}') {
            self.pos += 1;
            return Ok(Json::Object(map));
        }
        loop {
            self.skip_whitespace();
            let key = self.string()?;
            self.skip_whitespace();
            self.expect(b':')?;
            let value = self.value()?;
            map.insert(key, value);
            self.skip_whitespace();
            match self.peek() {
                Some(b',') => {
                    self.pos += 1;
                }
                Some(b'}') => {
                    self.pos += 1;
                    return Ok(Json::Object(map));
                }
                _ => return Err(format!("expected ',' or '}}' at byte {}", self.pos)),
            }
        }
    }
}

fn utf8_width(first: u8) -> usize {
    if first < 0x80 {
        1
    } else if first >> 5 == 0b110 {
        2
    } else if first >> 4 == 0b1110 {
        3
    } else {
        4
    }
}

// ---------------------------------------------------------------------------
// JSON-RPC client over the runtime's LSP-style framing
// ---------------------------------------------------------------------------

struct Runtime {
    child: Child,
    stdin: ChildStdin,
    stdout: BufReader<ChildStdout>,
    next_id: i64,
    events: Vec<Json>,
}

impl Runtime {
    fn start(argv: &[String]) -> Result<Self, String> {
        let mut child = Command::new(&argv[0])
            .args(&argv[1..])
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::inherit())
            .spawn()
            .map_err(|error| format!("failed to start '{}': {}", argv[0], error))?;

        let stdin = child.stdin.take().ok_or("runtime stdin unavailable")?;
        let stdout = BufReader::new(child.stdout.take().ok_or("runtime stdout unavailable")?);

        Ok(Runtime { child, stdin, stdout, next_id: 1, events: Vec::new() })
    }

    /// Writes one LSP-style frame. The runtime reads nothing without the
    /// Content-Length header, so every write goes through this framing.
    fn write_frame(&mut self, message: &Json) -> Result<(), String> {
        let body = message.to_json();
        write!(self.stdin, "Content-Length: {}\r\n\r\n", body.len())
            .map_err(|error| error.to_string())?;
        self.stdin.write_all(body.as_bytes()).map_err(|error| error.to_string())?;
        self.stdin.flush().map_err(|error| error.to_string())
    }

    fn read_frame(&mut self) -> Result<Json, String> {
        let mut content_length: Option<usize> = None;
        loop {
            let mut line = String::new();
            let read = self
                .stdout
                .read_line(&mut line)
                .map_err(|error| format!("failed to read headers: {}", error))?;
            if read == 0 {
                return Err("runtime closed the stream while reading headers".to_string());
            }
            let line = line.trim_end_matches(['\r', '\n']);
            if line.is_empty() {
                break;
            }
            if let Some((name, value)) = line.split_once(':') {
                if name.trim().eq_ignore_ascii_case("content-length") {
                    content_length = Some(
                        value
                            .trim()
                            .parse()
                            .map_err(|_| format!("invalid Content-Length header '{}'", value))?,
                    );
                }
            }
        }

        let length = content_length.ok_or("frame is missing a Content-Length header")?;
        let mut body = vec![0u8; length];
        self.stdout
            .read_exact(&mut body)
            .map_err(|error| format!("runtime closed the stream while reading the body: {}", error))?;
        let text = String::from_utf8(body).map_err(|_| "frame body is not UTF-8".to_string())?;
        Parser::parse(&text)
    }

    fn request(&mut self, method: &str, params: Json) -> Result<Json, String> {
        let id = self.next_id;
        self.next_id += 1;

        let mut message = BTreeMap::new();
        message.insert("jsonrpc".to_string(), Json::String("2.0".to_string()));
        message.insert("id".to_string(), Json::Number(id as f64));
        message.insert("method".to_string(), Json::String(method.to_string()));
        message.insert("params".to_string(), params);
        self.write_frame(&Json::Object(message))?;

        loop {
            let response = self.read_frame()?;

            if response.get("method").and_then(Json::as_str) == Some("session.event") {
                if let Some(event) = response.get("params").and_then(|p| p.get("event")) {
                    self.events.push(event.clone());
                }
                continue;
            }

            if response.get("id").and_then(Json::as_i64) != Some(id) {
                continue;
            }

            if let Some(error) = response.get("error") {
                let code = error.get("code").and_then(Json::as_i64).unwrap_or(0);
                let text = error.get("message").and_then(Json::as_str).unwrap_or("unknown error");
                return Err(format!("{}: {}", code, text));
            }

            return response.get("result").cloned().ok_or_else(|| "response has no result".to_string());
        }
    }

    fn assistant_message(&self) -> Option<&Json> {
        self.events.iter().find(|event| {
            event.get("type").and_then(Json::as_str) == Some("assistant.message")
        })
    }

    fn shutdown(mut self) {
        drop(self.stdin);
        let _ = self.child.wait();
    }
}

fn object(entries: Vec<(&str, Json)>) -> Json {
    let mut map = BTreeMap::new();
    for (key, value) in entries {
        map.insert(key.to_string(), value);
    }
    Json::Object(map)
}

fn run(argv: &[String]) -> Result<(), String> {
    let mut runtime = Runtime::start(argv)?;

    let connect = runtime.request("connect", object(vec![]))?;
    if connect.get("ok").and_then(Json::as_bool) != Some(true) {
        return Err(format!("connect did not report ok: {}", connect.to_json()));
    }

    let created = runtime.request(
        "session.create",
        object(vec![("sessionId", Json::String(SESSION_ID.to_string()))]),
    )?;
    if created.get("sessionId").and_then(Json::as_str) != Some(SESSION_ID) {
        return Err(format!("session.create returned {}", created.to_json()));
    }

    let sent = runtime.request(
        "session.sendMessages",
        object(vec![
            ("sessionId", Json::String(SESSION_ID.to_string())),
            (
                "messages",
                Json::Array(vec![object(vec![
                    ("role", Json::String("user".to_string())),
                    ("content", Json::String("Hello".to_string())),
                ])]),
            ),
        ]),
    )?;
    let message_ids = sent.get("messageIds").and_then(Json::as_array).unwrap_or(&[]);
    if message_ids.len() != 1 || message_ids[0].as_str() != Some(EXPECTED_MESSAGE_ID) {
        return Err(format!(
            "session.sendMessages returned {}, expected ['{}']",
            sent.to_json(),
            EXPECTED_MESSAGE_ID
        ));
    }

    let message = runtime
        .assistant_message()
        .ok_or("no assistant.message event was emitted")?;
    let content = message
        .get("data")
        .and_then(|data| data.get("content"))
        .and_then(Json::as_str)
        .unwrap_or("");
    if content != EXPECTED_CONTENT {
        return Err(format!("assistant.message content was '{}'", content));
    }

    // getMessages must replay the same transcript the events carried.
    let transcript = runtime.request(
        "session.getMessages",
        object(vec![("sessionId", Json::String(SESSION_ID.to_string()))]),
    )?;
    let replayed = transcript
        .get("events")
        .and_then(Json::as_array)
        .unwrap_or(&[])
        .iter()
        .any(|event| {
            event.get("type").and_then(Json::as_str) == Some("assistant.message")
                && event
                    .get("data")
                    .and_then(|data| data.get("content"))
                    .and_then(Json::as_str)
                    == Some(EXPECTED_CONTENT)
        });
    if !replayed {
        return Err("session.getMessages did not replay the recorded assistant message".to_string());
    }

    runtime.request("runtime.shutdown", object(vec![]))?;
    runtime.shutdown();

    println!("rust: PASS ({}: {})", EXPECTED_MESSAGE_ID, EXPECTED_CONTENT);
    Ok(())
}

fn main() {
    let argv: Vec<String> = std::env::args().skip(1).collect();
    if argv.is_empty() {
        eprintln!("usage: attach <runtime-command> [runtime-args...]");
        std::process::exit(2);
    }

    if let Err(error) = run(&argv) {
        eprintln!("rust: {}", error);
        std::process::exit(1);
    }
}
