# myBIReport · 可配置生产 BI 报表中心

一个本地运行的低代码 BI 报表中心。业务人员可在后台维护数据源、编写只读 SQL、自动识别查询参数与返回字段，并以表格、指标卡和 ECharts 图表组合发布报表。

## 已实现能力

- 报表中心：按一级分类组织报表，左侧目录可收起。
- SQL 驱动：报表 SQL 使用 `@参数名` 定义查询条件；后台校验 SQL 后自动识别参数和返回列。
- 统一查询页：自动生成文本或日期筛选项，展示结果表格，并支持 CSV 导出开关。
- 仪表盘布局：拖拽排序、3/6/9/12 栅格宽度；支持指标卡、柱状图、折线图、面积图、饼图。
- 图表配置：可绑定 X/Y 字段，设置标题、颜色、高度、图例、数据标签；前端采用 [Apache ECharts](https://echarts.apache.org/) 渲染。
- 多数据源管理：支持 SQLite、SQL Server、Oracle；可新增、测试连接并设为当前数据源。
- 通用数据库访问库：封装 SQLite、SQL Server、Oracle 的参数化执行与查询。

## 项目结构

```text
.
├─ BiDataAccess/                  通用数据库访问库
└─ ProductionProcessBi/
   ├─ Program.cs                   ASP.NET Core API 与报表执行逻辑
   ├─ Admin/                       独立后台配置前端
   ├─ Frontend/                    独立业务报表前端
   └─ data/                        本机数据源与报表配置（不提交 Git）
```

## 环境要求

- .NET 8 SDK 或更高版本
- 浏览器
- 可选：Python（用于快速启动两个静态前端）

## 本地启动

### 1. 启动 API

```powershell
cd ProductionProcessBi
dotnet run --urls http://127.0.0.1:5095
```

### 2. 启动业务报表前端

在另一个终端中：

```powershell
cd ProductionProcessBi\Frontend
python -m http.server 5173
```

打开 `http://127.0.0.1:5173`。

### 3. 启动后台配置前端

在第三个终端中：

```powershell
cd ProductionProcessBi\Admin
python -m http.server 5174
```

打开 `http://127.0.0.1:5174`。

API 默认地址为 `http://127.0.0.1:5095`，前端与后台均可通过 `window.PROCESS_BI_API_BASE` 覆盖。

## 配置数据源

进入后台的“数据源管理”，新增数据源、点击“测试连接”，确认成功后点击“设为当前”。

| 类型 | 连接信息示例 |
| --- | --- |
| SQLite | `Data Source=C:\\data\\report.db` |
| SQL Server | `Server=127.0.0.1;Database=ReportDb;User Id=sa;Password=你的密码;TrustServerCertificate=True` |
| Oracle | `User Id=用户名;Password=你的密码;Data Source=主机:1521/服务名` |

数据源连接信息及本地报表配置保存在 `ProductionProcessBi/data/`，该目录中的实际数据与连接配置已被 Git 忽略，避免上传生产数据或密码。

## 用户与权限

首次打开业务前端或管理后台时，按页面提示创建第一个系统管理员。之后由系统管理员在后台“用户与权限”中创建账号并分配角色：

- 系统管理员：管理用户、数据源和所有报表。
- 报表管理员：新建、编辑和发布报表，但不能管理用户或数据源。
- 报表使用者：只能查询分配给自己的报表，不能读取报表 SQL 或进入管理后台。

账号摘要和 Cookie 加密密钥保存在 `ProductionProcessBi/data/`，不会提交到 Git。遗失 `users.json` 会触发首次管理员创建流程；请备份本机数据目录。当前登录有效期为 8 小时。

权限登录适用于本机开发和可信内网验证。通过其他电脑访问前，请先为 API 配置 HTTPS；普通 HTTP 会明文传输登录信息和报表数据，不适合在不可信网络中使用。

## 创建报表

1. 在“报表管理”中新建报表，填写名称与分类。
2. 在“查询 SQL”中输入单条只读 `SELECT` SQL，例如：

```sql
SELECT date(IN_TIME) AS 日期, COUNT(*) AS 数量
FROM t_co_detail
WHERE date(IN_TIME) BETWEEN @startDate AND @endDate
GROUP BY date(IN_TIME)
ORDER BY date(IN_TIME)
```

3. 点击“校验 SQL 并读取字段”。系统会识别 `@startDate`、`@endDate` 及返回列。
4. 选择业务前端需要显示的查询条件、展示字段、CSV 导出开关和表格样式。
5. 在“仪表盘布局”中添加图表，绑定横轴“日期”和纵轴“数量”，保存即可发布。

## 安全说明

- 报表 SQL 仅允许单条 `SELECT` 或 `WITH` 查询，不允许分号及数据修改语句。
- 请勿把真实数据库密码、SQLite 文件或生产数据提交到 Git。
- 当前实现适合内网原型和本地部署；生产环境建议将连接凭据迁移至密钥管理服务，并加入账户、权限与审计功能。

## 后续方向

- 数据源字段字典与表结构浏览
- 更多图表与组件模板
- 账户、角色和报表访问权限
- 报表版本、发布审核和操作审计
