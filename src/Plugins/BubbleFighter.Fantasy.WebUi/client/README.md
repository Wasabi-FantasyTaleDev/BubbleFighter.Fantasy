# Administration interface

[中文](README.zh.md)

The optional administration interface uses Vue 3. Install its locked dependencies with `npm ci`, then run `npm run build`. Development uses `npm run dev`; configure its API endpoint for your local administration listener.

Enable administration only through its profile rows. Keep the listener on a trusted interface and configure authentication before exposing it. Do not place deployment credentials in frontend source or generated assets. See [configuration](../../../../docs/configuration.md).
