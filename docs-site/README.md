# Documentation site

The site uses Vue 3 and VitePress. Public content is maintained in `../docs/`; `pages.json` explicitly selects eleven guide routes in four languages. English uses `.md`, Simplified Chinese `.zh.md`, Traditional Chinese `.zh-tw.md` and Korean `.ko.md`. Traditional Chinese wording follows the site's existing terminology; the visible language label is 中文（繁体）.

## Develop and build

With Node.js and npm installed, run in this directory:

```sh
npm ci
npm test
npm run dev
npm run build
```

Development binds to `127.0.0.1:4273`. The content sync checks every translation and metadata before writing generated pages. Add or rename published routes through `pages.json`, theme navigation and all four source guides together. Local guide links are rewritten to the locale's routes; private and unpublished paths are rejected. Generated Markdown, dependencies and build output are ignored.

## Static hosting

For a repository subpath, set `DOCS_BASE` to a slash-delimited path, for example `/project/`, before `npm run build`. For a domain root, use `/`. Publish only `.vitepress/dist/` after reviewing its HTML, search assets and media. Build and preview the same base path before publication. The repository does not automatically publish the site.

The site icon and background video are local public assets. Review redistribution rights before a public release. Keep unpublished Windows downloads and Linux commands marked as forthcoming until real release endpoints are configured.
