//go:build !wasip1

package bffplugin

import "os"

// inline is false in a process: host requests run on their own goroutines and lines travel over stdio.
const inline = false

func diag(text string) { _, _ = os.Stderr.WriteString(text + "\n") }

func readHostLine() ([]byte, bool) { return nil, false }

func writeHostLine([]byte) {}
