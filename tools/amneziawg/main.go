// BYDLO's process host only. The protocol and cryptography are upstream AmneziaWG.
package main

import (
	"encoding/json"
	"fmt"
	"io"
	"os"
	"os/signal"
	"runtime"
	"strconv"
	"syscall"

	"github.com/amnezia-vpn/amneziawg-go/v3/conn"
	"github.com/amnezia-vpn/amneziawg-go/v3/device"
	"github.com/amnezia-vpn/amneziawg-go/v3/tun"
)

type config struct {
	Name string
	MTU  int
	UAPI string
}

func run() error {
	if len(os.Args) != 3 {
		return fmt.Errorf("usage: bydlo-amneziawg <config.json> <parent-pid>")
	}
	pid, err := strconv.Atoi(os.Args[2])
	if err != nil {
		return err
	}
	parent, err := watchParent(pid)
	if err != nil {
		return fmt.Errorf("parent unavailable")
	}
	b, err := os.ReadFile(os.Args[1])
	if err != nil {
		return fmt.Errorf("cannot read configuration")
	}
	var c config
	expectedName := "bydlo-awg"
	if runtime.GOOS == "darwin" {
		expectedName = "utun"
	}
	if json.Unmarshal(b, &c) != nil || c.Name != expectedName || c.MTU < 576 || c.MTU > 9000 {
		return fmt.Errorf("invalid configuration")
	}
	t, err := tun.CreateTUN(c.Name, c.MTU)
	if err != nil {
		return fmt.Errorf("create tunnel: %w", err)
	}
	d := device.NewDevice(t, conn.NewDefaultBind(), device.NewLogger(device.LogLevelSilent, ""))
	defer d.Close()
	// Never print errors returned by IpcSet: they can contain configuration secrets.
	if d.IpcSet(c.UAPI) != nil {
		return fmt.Errorf("AmneziaWG rejected configuration")
	}
	if d.Up() != nil {
		return fmt.Errorf("cannot bring tunnel up")
	}
	fmt.Println("BYDLO_AMNEZIA_READY")
	stop := make(chan os.Signal, 1)
	stdinClosed := make(chan struct{})
	go func() { io.Copy(io.Discard, os.Stdin); close(stdinClosed) }()
	signal.Notify(stop, os.Interrupt, syscall.SIGTERM)
	defer signal.Stop(stop)
	select {
	case <-parent:
	case <-stdinClosed:
	case <-stop:
	case <-d.Wait():
	}
	return nil
}

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, "FATAL:", err)
		os.Exit(1)
	}
}
