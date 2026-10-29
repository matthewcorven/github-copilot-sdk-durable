// Attaches to the deterministic replay runtime over stdio and completes one
// recorded turn. Standard library only: no module downloads required.
//
// Usage: go run . <runtime-command> [runtime-args...]
// The runtime command is supplied by run-all.sh so this check never guesses how
// the runtime is launched.
package main

import (
	"bufio"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"strconv"
	"strings"
)

const (
	sessionID         = "session-replay-1"
	expectedMessageID = "msg-0001"
	expectedContent   = "Hello from the recorded transcript."
)

type rpcMessage struct {
	JSONRPC string          `json:"jsonrpc"`
	ID      *int            `json:"id,omitempty"`
	Method  string          `json:"method,omitempty"`
	Params  json.RawMessage `json:"params,omitempty"`
	Result  json.RawMessage `json:"result,omitempty"`
	Error   *rpcError       `json:"error,omitempty"`
}

type rpcError struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
}

type sessionEvent struct {
	Type string `json:"type"`
	Data struct {
		Content   string `json:"content"`
		MessageID string `json:"messageId"`
	} `json:"data"`
}

type eventEnvelope struct {
	Event sessionEvent `json:"event"`
}

type runtime struct {
	cmd    *exec.Cmd
	stdin  io.WriteCloser
	stdout *bufio.Reader
	nextID int
	events []sessionEvent
}

func startRuntime(argv []string) (*runtime, error) {
	cmd := exec.Command(argv[0], argv[1:]...)
	cmd.Stderr = os.Stderr

	stdin, err := cmd.StdinPipe()
	if err != nil {
		return nil, err
	}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return nil, err
	}
	if err := cmd.Start(); err != nil {
		return nil, err
	}

	return &runtime{cmd: cmd, stdin: stdin, stdout: bufio.NewReader(stdout), nextID: 1}, nil
}

// writeFrame writes one LSP-style frame. The runtime reads nothing without the
// Content-Length header, so every write goes through this framing.
func (r *runtime) writeFrame(message any) error {
	body, err := json.Marshal(message)
	if err != nil {
		return err
	}
	if _, err := fmt.Fprintf(r.stdin, "Content-Length: %d\r\n\r\n", len(body)); err != nil {
		return err
	}
	_, err = r.stdin.Write(body)
	return err
}

func (r *runtime) readFrame() (*rpcMessage, error) {
	contentLength := -1
	for {
		line, err := r.stdout.ReadString('\n')
		if err != nil {
			return nil, fmt.Errorf("runtime closed the stream while reading headers: %w", err)
		}
		line = strings.TrimRight(line, "\r\n")
		if line == "" {
			break
		}
		name, value, found := strings.Cut(line, ":")
		if !found {
			continue
		}
		if strings.EqualFold(strings.TrimSpace(name), "content-length") {
			parsed, err := strconv.Atoi(strings.TrimSpace(value))
			if err != nil {
				return nil, fmt.Errorf("invalid Content-Length header %q", value)
			}
			contentLength = parsed
		}
	}

	if contentLength < 0 {
		return nil, errors.New("frame is missing a Content-Length header")
	}

	body := make([]byte, contentLength)
	if _, err := io.ReadFull(r.stdout, body); err != nil {
		return nil, fmt.Errorf("runtime closed the stream while reading the body: %w", err)
	}

	var message rpcMessage
	if err := json.Unmarshal(body, &message); err != nil {
		return nil, fmt.Errorf("failed to parse JSON-RPC message: %w", err)
	}
	return &message, nil
}

func (r *runtime) request(method string, params any) (json.RawMessage, error) {
	id := r.nextID
	r.nextID++

	if err := r.writeFrame(map[string]any{
		"jsonrpc": "2.0",
		"id":      id,
		"method":  method,
		"params":  params,
	}); err != nil {
		return nil, err
	}

	for {
		message, err := r.readFrame()
		if err != nil {
			return nil, err
		}

		if message.Method == "session.event" {
			var envelope eventEnvelope
			if err := json.Unmarshal(message.Params, &envelope); err != nil {
				return nil, err
			}
			r.events = append(r.events, envelope.Event)
			continue
		}

		if message.ID == nil || *message.ID != id {
			continue
		}
		if message.Error != nil {
			return nil, fmt.Errorf("%d: %s", message.Error.Code, message.Error.Message)
		}
		return message.Result, nil
	}
}

func (r *runtime) close() error {
	_ = r.stdin.Close()
	return r.cmd.Wait()
}

func (r *runtime) assistantMessage() *sessionEvent {
	for i := range r.events {
		if r.events[i].Type == "assistant.message" {
			return &r.events[i]
		}
	}
	return nil
}

func run(argv []string) error {
	r, err := startRuntime(argv)
	if err != nil {
		return err
	}
	defer func() { _ = r.close() }()

	var connect struct {
		OK bool `json:"ok"`
	}
	result, err := r.request("connect", map[string]any{})
	if err != nil {
		return err
	}
	if err := json.Unmarshal(result, &connect); err != nil {
		return err
	}
	if !connect.OK {
		return fmt.Errorf("connect did not report ok: %s", result)
	}

	var created struct {
		SessionID string `json:"sessionId"`
	}
	result, err = r.request("session.create", map[string]any{"sessionId": sessionID})
	if err != nil {
		return err
	}
	if err := json.Unmarshal(result, &created); err != nil {
		return err
	}
	if created.SessionID != sessionID {
		return fmt.Errorf("session.create returned %q, expected %q", created.SessionID, sessionID)
	}

	var sent struct {
		MessageIDs []string `json:"messageIds"`
	}
	result, err = r.request("session.sendMessages", map[string]any{
		"sessionId": sessionID,
		"messages":  []map[string]string{{"role": "user", "content": "Hello"}},
	})
	if err != nil {
		return err
	}
	if err := json.Unmarshal(result, &sent); err != nil {
		return err
	}
	if len(sent.MessageIDs) != 1 || sent.MessageIDs[0] != expectedMessageID {
		return fmt.Errorf("session.sendMessages returned %v, expected [%q]", sent.MessageIDs, expectedMessageID)
	}

	message := r.assistantMessage()
	if message == nil {
		return errors.New("no assistant.message event was emitted")
	}
	if message.Data.Content != expectedContent {
		return fmt.Errorf("assistant.message content was %q", message.Data.Content)
	}

	// getMessages must replay the same transcript the events carried.
	var transcript struct {
		Events []sessionEvent `json:"events"`
	}
	result, err = r.request("session.getMessages", map[string]any{"sessionId": sessionID})
	if err != nil {
		return err
	}
	if err := json.Unmarshal(result, &transcript); err != nil {
		return err
	}
	replayed := false
	for _, event := range transcript.Events {
		if event.Type == "assistant.message" && event.Data.Content == expectedContent {
			replayed = true
			break
		}
	}
	if !replayed {
		return errors.New("session.getMessages did not replay the recorded assistant message")
	}

	if _, err := r.request("runtime.shutdown", map[string]any{}); err != nil {
		return err
	}

	fmt.Printf("go: PASS (%s: %s)\n", expectedMessageID, expectedContent)
	return nil
}

func main() {
	argv := os.Args[1:]
	if len(argv) == 0 {
		fmt.Fprintln(os.Stderr, "usage: go run . <runtime-command> [runtime-args...]")
		os.Exit(2)
	}

	if err := run(argv); err != nil {
		fmt.Fprintf(os.Stderr, "go: %v\n", err)
		os.Exit(1)
	}
}
