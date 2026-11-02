package main

import "golang.org/x/sys/windows"

func watchParent(pid int) (<-chan struct{}, error) {
	h, err := windows.OpenProcess(windows.SYNCHRONIZE, false, uint32(pid))
	if err != nil {
		return nil, err
	}
	done := make(chan struct{})
	go func() { defer windows.CloseHandle(h); windows.WaitForSingleObject(h, windows.INFINITE); close(done) }()
	return done, nil
}
