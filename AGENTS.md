# AGENTS.md — QianShi PaperTodo 插件仓库

本文件是本仓库的**入口、架构说明与 AI Agent 执行规则**。改代码前先读「4. 架构」与「6. AI Agent 工作约束」；
动到数据路径前必须先读「4.4 关键不变量」。

## 1. 这个仓库是什么

为 [PaperTodo](https://github.com/snownico0722/PaperTodo) 编写原生（Native）插件的仓库。
仓库只维护**自己的插件代码**；宿主源码通过 git 子模块引入，只作为编译期契约来源。

当前包含一个插件：**卡片归档（`qianshi.archive.native`）** ——
把待办/笔记卡片从界面移除而不删除数据，并提供一键恢复、删除兜底回收站与"归档锁定"。

## 2. 项目知识入口

| 想知道什么 | 读哪里 |
| --- | --- |
| 这个仓库怎么分层、有哪些硬约束 | 本文件第 3、4 节 |
| 插件怎么用、有哪些能力边界、怎么构建安装 | [`README.md`](README.md) |
| 宿主如何定义插件契约（API 2.2） | 子模块 `PaperTodo\PaperTodo.Plugin.Abstractions\*.cs` |
| 宿主插件开发手册 | 子模块 `PaperTodo\plugin-samples\README.md` |
| 宿主自身架构与历史取舍 | 子模块 `PaperTodo\doc\ARCHITECTURE.md`、`doc\DECISIONS.md` |
| 宿主行为的具体实现 | 子模块 `PaperTodo\src\`（只读参考，不要改） |

**默认只读与当前任务相关的部分**，不要把整个宿主源码当必读材料。

## 3. 仓库结构

```text
QianShi.PaperTodo.Plugins\
├─ QianShi.PaperTodo.Plugins.slnx     解决方案（.slnx：src 插件 + tests 检查 + 外部契约项目）
├─ src\
│  ├─ Directory.Build.props           所有插件共用的编译属性
│  └─ QianShi.PaperTodo.Archive\      卡片归档插件（一个插件一个目录）
├─ tests\
│  └─ QianShi.PaperTodo.Archive.Checks\ 离线行为检查程序（内存假宿主，当前 35 项）
├─ build\
│  └─ Build-And-Install-NativePlugin.ps1 通用打包 + 安装脚本（自动发现 src\ 下所有插件）
├─ dist\                              打包产物（不入库）
├─ PaperTodo\                         宿主源码，**git 子模块**，钉在固定提交
└─ AGENTS.md / README.md / .gitmodules / .gitignore / .gitattributes
```

新增插件的约定见 [`README.md`](README.md) 的「新增一个插件」，脚本与解决方案都不需要改。

## 4. 架构

### 4.1 宿主的插件模型（本仓库一切设计的边界）

- 一个 Native 插件 = 一个 DLL，含**恰好一个** public、非抽象、无参构造的 `IPaperBodyPlugin` 实现；
  `plugin.json` 是 id / 名称 / 版本 / `apiVersion` / 权限 / 设置 / 启动卡片的**唯一权威**。
- **Body / Mini 是前端，Provider Runtime 是后端**。一个 provider 最多一个 Runtime；
  Runtime 的存活条件是「该 provider 至少存在一张真实 Note 卡片」，最后一张离开即释放（1→0）。
  隐藏/折叠/Body 重建都**不影响** Runtime。
- 插件贡献的 UI（全局顶栏按钮、卡片右键菜单、Todo 行菜单、全局快捷键）**全部属于 Runtime**：
  没有该 provider 的卡片 ⇒ Runtime 不存在 ⇒ 所有入口消失。宿主没有"不持有卡片也能贡献 UI"的机制。
- 宿主数据（`data.json`、图片 LMDB）由宿主拥有；插件通过 `IPaperTodoHostApi` /
  `IPaperWorkspacePresentationApi` 等受控接口读写，**不得**自行建立第二套权威状态。
- 事件订阅是事后通知（没有"删除前"钩子），`ExcludeOwnOperations` 默认排除插件自身操作。

### 4.2 归档插件的分层

| 文件 | 职责 |
| --- | --- |
| `ArchivePlugin.cs` | 入口工厂：`IPaperBodyPlugin` + `IPaperPluginRuntimeProvider`，无状态 |
| `ArchiveRuntime.cs` | Provider Runtime：事件镜像、回收站捕获、顶栏/右键/Todo 行贡献、**归档锁定扫描**、设置订阅、Dispose 清理 |
| `ArchiveService.cs` | 业务实现：归档 / 恢复 / 彻底删除 / 单条待办归档 / 打开归档中心，供 Runtime 与界面共用 |
| `ArchiveSession.cs` | 归档中心卡片界面（WPF），只调用 `IArchiveOperations` |
| `ArchiveStore.cs` | 插件自有存储 `<plugin>\.runtime\archive-store.json` 的实现 |
| `ArchiveMirror.cs` / `ArchiveRestore.cs` | 镜像捕获与"从冻结快照重建宿主内容" |
| `ArchiveModels.cs` / `ArchiveContracts.cs` | 记录模型、仓库与操作接口、结果码 |
| `ArchiveSettings.cs` / `ArchiveText.cs` | 设置解析校验；四语言文案（zh/en/ja/ko） |

数据流：宿主事件/用户操作 → `ArchiveRuntime`（镜像与捕获）→ `IArchiveRepository`（持久化）→
`ArchiveService`（业务）→ 宿主接口（hide/show/create/delete）。界面只读仓库、只调操作。

### 4.3 数据与 ownership

- 归档记录、回收站、镜像、保留策略**全部**在 `.runtime\archive-store.json`，不写 `data.json`，
  不使用宿主 `IPaperPluginRuntimeState`。
- 写入方式：写临时文件 → 原子替换；读失败时把原文件复制成 `.corrupt-<时间戳>` 再继续；
  读到**比自己新**的文档版本时整体锁定为**只读**，拒绝用旧格式覆盖。
- 归档 = `papers.hide`（`IsVisible=false`），数据完整留在宿主；恢复 = `papers.show`。
- 只有「彻底删除」会调用宿主 `DeletePaper`；只有「归档单条待办」会调用 `DeleteTodo`。

### 4.4 关键不变量（改代码前必读）

1. **归档路径绝不删除数据**：只 hide。任何新增的"顺便清理"都视为数据安全缺陷。
2. `DeletePaper` 在全仓库**只有一个调用点**（`PurgeEntry`，用户明确操作 + 界面强制确认）。
3. `DeleteTodo` 前必须先把该行**冻结进记录**；恢复走 `AppendTodos`。
4. 恢复顺序：**先**把记录标记为 Restored（清空归档索引）**再** `ShowPaper`；`ShowPaper` 失败必须回滚为 Archived。
5. 归档锁定：可见性事件按"合并成一次扫描"处理，不得每张卡各扫一次；不得形成自激循环。
6. 插件自己的卡片（`BodyProviderId == 本插件`）不可归档、不可进锁定扫描、不参与镜像。
7. 宿主没有"归档"状态，归档与普通隐藏在宿主侧不可区分——所有过滤必须靠插件自己的归档索引。
8. 订阅回调、定时器、设置回调里抛异常不得传播到宿主；Dispose 必须取消待执行扫描且可重入安全。

### 4.5 运行环境与版本约束

- 插件面向 **Plugin API 2.2**，依赖 `papers.hide/show`（跨卡片显示控制）与
  `startupPaper.presentation: "hidden"`。**已发布的 PaperTodo 3.4 只支持到 2.1，装不了本插件**
  （表现为"装上去了但没有任何按钮"），必须使用支持 2.2 的宿主构建。
- 编译期契约取自 `PaperTodo\` 子模块的**固定提交**；升级宿主 = 升级子模块指针，且**必须重跑检查程序**。
- 宿主源码自己还带 `vendor\wpf-notifyicon` 子模块；克隆本仓库要用
  `git clone --recurse-submodules`，否则宿主编不过（插件本身编译不受影响）。

## 5. 已知不可行 / 不要重复尝试

这些结论都有宿主源码依据，动手前别再走一遍：

- **笔记图片无法恢复到重建后的卡片**：宿主图片资产带 `NoteId` 归属，渲染时要求
  `asset.NoteId == 当前卡片`，且宿主没有"加载时重新认领"的逻辑；插件 API 只有只读
  `ReadImage`，没有写入/导入接口。把 `i:` 引用写进另一张卡片只能**保住字节、保住编号不被复用**，
  解决不了归属 ⇒ 图片仍不显示。唯一彻底解法是宿主提供 `WriteImage(paperId, mime, bytes) → imageId`。
- **无法拦截宿主的删除**：没有 pre-delete 钩子；`hiddenHostActions` 只能隐藏"新建待办/新建笔记"。
  真正的兜底是"删除事件 + 之前维护好的镜像"。
- **插件不能创建属于自己 provider 的卡片**：`CreatePaperRequest` 没有 `BodyProviderId` 字段。
  因此删除归档中心卡片后，插件**无法在运行中自愈**，只能靠重启时 `startupPaper`，
  或用户右键笔记卡片 →「正文类型」→「卡片归档」。
- **宿主的"显示全部"没有过滤能力**：托盘双击、托盘菜单按钮、Show/Hide/Toggle 快捷键都直接
  `State.Papers` 全显示。归档锁定的"先显示再收回"是有意接受的代价。
- **不能绕过 `StateStore` 改 `data.json`，也不能直接开 LMDB 事务。**

## 6. AI Agent 工作约束

### 6.1 动手前

- 先读相关当前代码与契约文件，再改；不要凭本文件或旧记忆推断实现。
- 不确定宿主行为时，去子模块 `PaperTodo\src\` 找证据（本文件第 4、5 节的结论都来自那里）。
- 判断"当前该按什么设计"看本文件与 `README.md`；判断"具体怎么改"必须回到代码。

### 6.2 边界与禁区

- **`PaperTodo\` 是子模块，只读**：不得修改、不得在其中放构建产物、不得改 `.gitmodules` 指向。
  需要给宿主加能力时，写成提案交给上游，不要在插件里复制宿主实现。
- 不要给插件引入 **NuGet 依赖**：`tests\` 必须离线可构建，插件也不应增加加载期风险。
- UI 只用 WPF 标准控件；**不得**新开 `Window`、`MessageBox`、独立 `ContextMenu`/`Popup`
  （确认类交互做在卡片内的浮层里）。
- 不得为了"测试方便"降低权限、数据隔离或秘密保护要求；不得把真实邮箱、凭据写进任何文件。
- 不得把临时探针、一次性脚本、诊断脚手架留在仓库里作为最终交付；要么转成正式检查，要么删除。
- 不得删除或弱化既有的数据保护路径来"简化"实现。

### 6.3 验证

按**行为风险**决定验证规模，不是按改动行数：

```powershell
# 编译（解决方案）
dotnet build .\QianShi.PaperTodo.Plugins.slnx -c Release

# 行为检查（必须全部通过，当前 35 项；以运行输出为准）
dotnet run --project .\tests\QianShi.PaperTodo.Archive.Checks -c Release

# 打包 / 安装（安装前必须退出 PaperTodo）
pwsh -File .\build\Build-And-Install-NativePlugin.ps1 -SkipInstall
```

- 改到持久化、恢复/删除、归档锁定、Runtime 贡献注册时，**必须**新增或更新检查项，不接受"只靠阅读确认"。
- 改到界面时，至少确认窄卡片（~336 DIP）不裁切、明暗主题都可读、所有文案走 `ArchiveText`。
- 检查程序跑完不得在仓库或插件目录留下 `.runtime`、临时目录。

### 6.4 文案与权限

- 所有用户可见字符串走 `ArchiveText`，**四种语言（zh/en/ja/ko）键集必须完全一致**，不得只加一种语言。
- `plugin.json` 的权限按需最小化；新增权限必须在提交正文里说明用途。
- 设置项的 `id` 必须与 `ArchiveSettings` 常量一致；数值型默认值必须落在 `min/max` 内且与 `step` 对齐。

### 6.5 文档同步

| 变化 | 更新 |
| --- | --- |
| 用户可见行为、能力边界、安装方式 | `README.md` |
| 分层、ownership、关键不变量、运行环境约束 | 本文件 |
| 一次性排查结论、为什么某方案不可行 | 本文件第 5 节（附代码依据） |

没有影响就不要为了"同步过"制造改动；不要新增与本文件重复描述"当前架构"的并行文档。

### 6.6 隐私

- 提交身份固定为 `qianshi <43244500+qianshipoi@users.noreply.github.com>`，配置在**本仓库 local**。
- 不要在文件、提交信息、脚本里写入真实邮箱、令牌或本机绝对路径。

## Git 提交约定

- 提交消息采用 Conventional Commits 结构：`type(scope): 中文简述`。
- `type` 使用 `feat`、`fix`、`refactor`、`perf`、`test`、`docs`、`style`、`build`、`ci` 或 `chore`。
- `scope` 使用受影响的模块或项目名；影响范围不明确时可以省略。
- 首行使用中文、简明描述实际变更，不以句号结尾，建议控制在 50 个字符以内。
- 首行后需要补充背景、实现方式、兼容性或验证信息时，空一行后书写正文。
- 关联问题或破坏性变更时，在正文末尾使用 `Refs: #编号`、`Closes: #编号` 或 `BREAKING CHANGE:`。
- 提交按相关变化的类别或模块组织，不需要每个小变化都生成一条记录，也不得机械地按文件拆分提交。同一类变化或同一模块内的相关改动可以合并为一条提交，在正文中描述具体细节及验证结果。
- 只有目的独立、需要单独审查或回滚的变化才拆分提交；不混入无关格式化、临时文件或不相关的功能。
- 新增公共 API 或数据契约时，应考虑向后兼容。
- 对破坏性变更必须明确说明影响范围和迁移方案。
- 不得因为测试方便而降低认证、授权、数据隔离或秘密保护要求。

可使用 `Auth`、`DynSec`、`Audit`、`UI`、`publish` 或项目名等实际影响范围，例如 `docs: 完善中文项目协作指南`、`fix(Auth): 修复会话过期后的登录跳转`。版本发布相关提交使用适合变更内容的 `build` 或 `chore` 等类型，不另造 `release` 类型。
