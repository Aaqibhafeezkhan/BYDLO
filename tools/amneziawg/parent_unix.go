//go:build !windows

package main

import (
	"fmt"
	"os"
	"time"
)

func watchParent(pid int) (<-chan struct{}, error) {
	if os.Getppid() != pid {
		return nil, fmt.Errorf("parent mismatch")
	}
	done := make(chan struct{})
	go func() {
		for os.Getppid() == pid {
			time.Sleep(time.Second)
		}
		close(done)
	}()
	return done, nil
}
