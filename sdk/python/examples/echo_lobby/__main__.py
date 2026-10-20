"""Entry point: ``python -m echo_lobby`` serves the plugin over stdio."""

from bff_plugin import run

import echo_lobby  # noqa: F401  (declares the plugin class)

run()
