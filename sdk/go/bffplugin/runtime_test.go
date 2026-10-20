package bffplugin

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

func TestLifetimeNotificationsAreIsolatedAndShutdownCancelsEveryRow(t *testing.T) {
	first, cancelFirst := context.WithCancel(context.Background())
	second, cancelSecond := context.WithCancel(context.Background())
	defer cancelFirst()
	defer cancelSecond()
	rt := &runtime{active: map[string]*activation{
		"a": {cancelLifetime: cancelFirst}, "b": {cancelLifetime: cancelSecond},
	}}
	for _, id := range []string{"missing", "a", "a"} {
		msg := map[string]json.RawMessage{"method": json.RawMessage(`"lifecycle.cancel"`),
			"params": json.RawMessage(`{"activation":"` + id + `"}`)}
		if rt.dispatch(msg) {
			t.Fatal("cancellation stopped the shared runtime")
		}
	}
	if first.Err() == nil || second.Err() != nil || len(rt.active) != 2 {
		t.Fatal("cancellation crossed activation ownership or retained an unknown id")
	}
	rt.shutdown()
	if second.Err() == nil {
		t.Fatal("shutdown did not cancel the remaining activation")
	}
}

type gatedWriter struct {
	entered chan struct{}
	release chan struct{}
	writes  atomic.Int32
}

func (w *gatedWriter) Write(data []byte) (int, error) {
	if w.writes.Add(1) == 1 {
		close(w.entered)
		<-w.release
	}
	return len(data), nil
}

func TestResponseWaitersKeepAdmissionUntilTheyOwnOutput(t *testing.T) {
	writer := &gatedWriter{entered: make(chan struct{}), release: make(chan struct{})}
	rt := &runtime{out: writer}
	first := make(chan error, 1)
	go func() { first <- rt.write(map[string]any{"result": "first"}) }()
	<-writer.entered
	defer close(writer.release)
	completed := make(chan struct{})
	second := make(chan error, 1)
	go func() {
		second <- rt.writeCompleted(map[string]any{"result": "second"}, func() { close(completed) })
	}()
	select {
	case <-completed:
		t.Fatal("a response waiting for output released its request admission")
	case <-time.After(50 * time.Millisecond):
	}
	writer.release <- struct{}{}
	for _, result := range []chan error{first, second} {
		select {
		case err := <-result:
			if err != nil {
				t.Fatal(err)
			}
		case <-time.After(2 * time.Second):
			t.Fatal("response did not recover after output resumed")
		}
	}
	select {
	case <-completed:
	default:
		t.Fatal("written response retained request admission")
	}
}

type failingWriter struct{}

func (failingWriter) Write([]byte) (int, error) { return 0, io.ErrClosedPipe }

func TestResponseFailureReturnsAdmissionExactlyOnce(t *testing.T) {
	for _, message := range []map[string]any{
		{"result": make(chan int)},
		{"result": strings.Repeat("x", MaxLineBytes)},
		{"result": "closed output"},
	} {
		rt := &runtime{out: failingWriter{}}
		completed := 0
		err := rt.writeCompleted(message, func() { completed++ })
		if err == nil || completed != 1 {
			t.Fatalf("failure=%v, admission refunds=%d", err, completed)
		}
		if message["result"] == "closed output" && !errors.Is(err, io.ErrClosedPipe) {
			t.Fatalf("original writer failure lost: %v", err)
		}
	}
}
