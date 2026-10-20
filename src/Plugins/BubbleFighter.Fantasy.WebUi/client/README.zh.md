# 管理界面

[English](README.md)

可选管理界面使用 Vue 3。先用 `npm ci` 安装锁定的依赖，再运行 `npm run build`。开发时运行 `npm run dev`，并将 API 地址配置为本地管理监听器。

通过 profile 配置行启用管理功能。监听器应绑定可信接口，对外开放前配置认证。不要把部署凭证放进前端源码或生成资源。参阅[配置指南](../../../../docs/configuration.zh.md)。
