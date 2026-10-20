//go:build wasip1

package bffplugin

import "unsafe"

// inline is true in a module (GOOS=wasip1, run by loader.wasm): the module has one thread, so host requests are
// served one at a time and lines travel through the host imports bff.recv, bff.send and bff.log.
const inline = true

//go:wasmimport bff recv
func bffRecv(ptr unsafe.Pointer, capacity int32) int32

//go:wasmimport bff send
func bffSend(ptr unsafe.Pointer, length int32)

//go:wasmimport bff log
func bffLog(ptr unsafe.Pointer, length int32)

var recvBuffer = make([]byte, 64*1024)

// readHostLine blocks for the host's next line; false at end of input. A line longer than the buffer stays
// pending on the host, which reports its length, so the buffer grows and the call repeats.
func readHostLine() ([]byte, bool) {
	for {
		n := bffRecv(unsafe.Pointer(&recvBuffer[0]), int32(len(recvBuffer)))
		if n == 0 {
			return nil, false
		}
		if n < 0 {
			recvBuffer = make([]byte, -n)
			continue
		}
		line := make([]byte, n)
		copy(line, recvBuffer[:n])
		return line, true
	}
}

func writeHostLine(line []byte) {
	if len(line) == 0 {
		return
	}
	bffSend(unsafe.Pointer(&line[0]), int32(len(line)))
}

func diag(text string) {
	if text == "" {
		return
	}
	b := []byte(text)
	bffLog(unsafe.Pointer(&b[0]), int32(len(b)))
}
