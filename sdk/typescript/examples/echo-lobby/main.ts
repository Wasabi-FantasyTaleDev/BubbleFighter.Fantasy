/** Entry point: `node dist/examples/echo-lobby/main.js` serves the plugin over stdio. */

import { run } from "../../src/index.js";

import "./echo-lobby.js"; // declares the plugin class

run();
