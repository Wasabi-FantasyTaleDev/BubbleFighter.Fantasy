"""``python -m bf_item_viewer``: no arguments or ``gui`` opens the graphical viewer; other subcommands are the CLI."""

import sys


def run() -> int:
    args = sys.argv[1:]
    if not args or args[0] == "gui":
        from .ui.app import main as gui_main
        return gui_main(args[1:])
    from bf_item_viewer.cli import main as cli_main
    return cli_main(args)


if __name__ == "__main__":
    sys.exit(run())
