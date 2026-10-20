// Exercises declared service calls against a development memory wallet store.
package main

import (
	"bffplugin/bffplugin"
	"encoding/json"
	"fmt"
	"os"
)

type wallet struct {
	UserNum int64 `json:"user_num"`
	Lucci   int32 `json:"lucci"`
	Cash    int32 `json:"cash"`
}

type plugin struct{}

func (*plugin) Activate(ctx *bffplugin.Context) error {
	want := wallet{UserNum: 9007199254740993, Lucci: 73, Cash: 19}
	for i := 0; i < 2; i++ {
		if _, err := ctx.Services.Call("IPlayerWalletRepository", "UpsertAsync", map[string]any{"wallet": want}); err != nil {
			return err
		}
	}
	result, err := ctx.Services.Call("IPlayerWalletRepository", "FindByUserNumAsync", map[string]any{"user_num": want.UserNum})
	if err != nil {
		return err
	}
	var actual wallet
	if err := json.Unmarshal(result, &actual); err != nil {
		return err
	}
	if actual != want {
		return fmt.Errorf("wallet round trip lost data")
	}
	return nil
}

func main() {
	bffplugin.Register(bffplugin.Definition{ID: "example.services-wallet", Version: "1.0.0", Requires: []string{"IPlayerWalletRepository"}}, func() bffplugin.Plugin { return &plugin{} })
	os.Exit(bffplugin.Run())
}
