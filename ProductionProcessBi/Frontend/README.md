# ProductionProcessBi 前台

生产部署不需要把此前端目录复制到另一台电脑，也不需要启动 Python 代理。发布主服务后，浏览器直接访问：

- 前台：`http://<服务器地址>:5095/`
- 后台：`http://<服务器地址>:5095/admin/`

两个页面与 API 都由同一个 ASP.NET Core 服务提供，前端请求默认使用当前页面的同源 `/api` 地址，登录 Cookie 无需跨域配置。

`start-frontend.ps1` 和 `server.py` 仅保留为本地开发兼容工具，不属于生产发布产物。
