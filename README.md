# PaperTodo 卡片归档插件

给 [PaperTodo](../PaperTodo) 写的一个原生插件：把待办卡片和笔记卡片**归档**起来。

归档只让卡片从界面消失，**数据一条都不删**；想找回来时在「归档中心」里一键恢复。
另外插件会持续备份卡片内容，所以即使你在 PaperTodo 里直接点了删除，也能从回收站把内容找回来。

---

## 目录结构

```text
QianShi.PaperTodo.Plugins\               本仓库根目录
├─ QianShi.PaperTodo.Plugins.slnx        解决方案（src\ 插件 + tests\ 检查程序 + 外部契约项目）
├─ src\
│  ├─ Directory.Build.props              所有插件共用的编译属性（TFM / Nullable / Release 去符号）
│  └─ QianShi.PaperTodo.Archive\         卡片归档插件（一个插件一个目录）
│     ├─ plugin.json                     插件清单（id / 权限 / 设置 / 启动卡片）
│     ├─ ArchivePlugin.cs                入口：IPaperBodyPlugin + IPaperPluginRuntimeProvider
│     ├─ ArchiveRuntime.cs               Provider Runtime：事件镜像、回收站捕获、顶栏/右键菜单、归档锁定
│     ├─ ArchiveService.cs               归档 / 恢复 / 彻底删除的业务实现
│     ├─ ArchiveStore.cs                 插件自有存储 .runtime/archive-store.json（原子写入）
│     ├─ ArchiveSession.cs               归档中心卡片界面（WPF）
│     ├─ ArchiveModels.cs                记录模型
│     ├─ ArchiveContracts.cs             仓库与操作接口、结果码
│     ├─ ArchiveSettings.cs              设置读取与校验
│     └─ ArchiveText.cs                  中文 / English / 日本語 / 한국어 文案
├─ tests\
│  └─ QianShi.PaperTodo.Archive.Checks\  离线行为检查程序（内存假宿主，无需启动 PaperTodo）
├─ build\
│  └─ Build-And-Install-NativePlugin.ps1 通用打包 + 安装脚本（自动发现 src\ 下所有插件）
├─ dist\                                 打包产物（自动生成）
├─ PaperTodo\                            PaperTodo 官方源码（**git 子模块**，钉在固定提交）
├─ .gitmodules                           子模块声明（path = PaperTodo，url = 官方仓库，branch = main）
├─ .gitattributes / .gitignore
└─ README.md                             本文件
```

> 解决方案用新版 `.slnx` 格式（XML、可读、结构即目录）：.NET 9+ 的 `dotnet build` / `dotnet sln`
> 和 VS 2022 17.13+、Rider 都能直接打开。它把外部契约项目放在 `PaperTodo` 文件夹下，
> 便于 F12 跳转，同时一眼能看出那不是本仓库的代码。

### 官方源码作为 git 子模块

`PaperTodo\` 是本仓库的 **git 子模块**，不是散落在仓库里的一份拷贝：

- 子模块只记录**一个提交指针**，官方源码的内容不进入本仓库历史；本仓库只维护 `src\` / `tests\` / `build\`。
- 插件编译期引用的就是这份被钉住的源码（`..\..\PaperTodo\PaperTodo.Plugin.Abstractions\`），
  所以"编译时的契约版本"永远是确定的、可复现的。

全新克隆：

```powershell
git clone --recurse-submodules <本仓库地址>
# 已经克隆过但忘了子模块：
git submodule update --init --recursive
```

> `--recursive` 是必需的：官方源码自己还带一个 `vendor\wpf-notifyicon` 子模块，
> 缺了它宿主源码编译不过（插件本身编译不受影响）。

主动升级宿主版本（**升级后必须重跑检查程序**，因为契约可能变）：

```powershell
git submodule update --remote PaperTodo      # 拉到 .gitmodules 里配置的 main 最新
git add PaperTodo                            # 记录新的提交指针
git commit -m "chore: 升级 PaperTodo 子模块"
```

如果宿主的契约只在一份本地改动里（还没推到远端），把那份改动推到官方仓库/你的 fork，
再用同一个提交指针，否则别人 `submodule update` 拿不到那份代码。

### 新增一个插件

仓库存放多个插件时，约定只有三条，脚本和解决方案都不需要改：

1. 在 `src\<插件项目名>\` 建项目，放一个 `plugin.json` 和一个同名 `.csproj`；
   `.csproj` 只需要 `AssemblyName` / `RootNamespace` / `Version`、`UseWPF`（如果需要 WPF 面）
   以及对 `..\..\PaperTodo\PaperTodo.Plugin.Abstractions\` 的引用，
   其余编译属性由 `src\Directory.Build.props` 统一提供。
2. 需要验证的话在 `tests\<插件项目名>.Checks\` 加一个控制台检查项目（不引 NuGet）。
3. 加进解决方案：`dotnet sln .\QianShi.PaperTodo.Plugins.slnx add src\<插件项目名>\<插件项目名>.csproj`。

之后 `build\Build-And-Install-NativePlugin.ps1` 会自动发现并打包 `src\` 下**所有**带 `plugin.json`
的项目；只想打一个时用 `-Plugin <项目名>`。

---

## 为什么需要它

PaperTodo 的产品形态是「桌面上的几张纸」，卡片一多，桌面上就会堆满内容。
但宿主里把卡片「移除」只有一条路——删除。而删除是真的删数据：纸片从 `data.json` 里被移除，
其它待办对它的 `LinkedPaperId` 关联会被清空，笔记里的图片随后会被当成无引用资源回收。
删完之后没有任何入口可以找回。

这个插件补上那条路：

| 你的目的 | 以前 | 现在 |
| --- | --- | --- |
| 桌面太乱，先收起来几张 | 只能删掉，数据没了 | 点「归档」，卡片隐藏，数据完整保留 |
| 过几天想找回 | 找不回来 | 归档中心一键恢复 |
| 手滑删掉了一张卡片 | 数据丢失 | 回收站里恢复内容 |
| 真的永久不要了 | 删除 | 归档中心「彻底删除」 |

---

## 三条能力

### 1. 归档卡片（不删数据）

在任意待办 / 笔记卡片的**顶栏**点归档按钮，或在卡片**右键菜单**里选「归档此卡片」。

插件做的事是：

1. 先把这张卡片的完整内容（标题、全部待办条目及其完成状态 / 顺序 / 提醒 / 关联、笔记正文）快照进插件自己的存储；
2. 再调用宿主的 `papers.hide` 把卡片从界面隐藏。

隐藏后的卡片仍然完整存在于 PaperTodo 的 `data.json` 里，只是 `IsVisible = false`。
所以「恢复」不需要重建内容，只是把同一张卡片重新显示出来，id 不变、关联关系全部保留。

### 2. 归档中心：一键恢复

归档中心是插件自己的卡片（一个 PaperTodo 纸片），启动时由插件清单里的 `startupPaper`
自动创建并**默认隐藏**，不占你的桌面。

打开方式：任意卡片顶栏的归档中心按钮，或卡片右键菜单里的「打开归档中心」。

里面有四个列表：全部 / 已归档 / 回收站 / 已恢复，支持按标题或内容搜索，
每条记录可以直接「恢复」或「彻底删除」，也可以一次「全部恢复」。

### 3. 回收站：接管宿主删除

宿主的删除按钮插件拦不住（插件 API 没有「删除前」钩子），所以插件用另一种方式兜底：
Provider Runtime 持续把你所有卡片的内容镜像到 `.runtime/`。等你真的在 PaperTodo 里删除了
某张卡片，插件收到删除事件时已经从镜像里拿到了内容，于是把它记进回收站。

- 待办卡片：删除事件本身会带上完整条目，回收站里是精确内容；
- 笔记卡片：删除事件不带正文，靠镜像恢复，可能是删除前最后一次镜像的内容。

恢复时如果原卡片已经不存在，插件会用快照重建一张新卡片（新 id），
并自动清理指向已不存在卡片的 `LinkedPaperId` 关联。

### 4. 归档锁定：挡住宿主的「显示全部」

宿主本身有三条"整体显示"的路径，而且**没有任何过滤或设置**：
托盘双击（`ShowAllPapers()` 直接对 `State.Papers` 全显示）、托盘菜单里的显示/隐藏按钮、
以及 Show/Hide/Toggle 全局快捷键（它只为"被待办链接的纸片"保留了快照保护）。

而"归档"在宿主看来就是 `IsVisible = false`，和普通隐藏无法区分——所以默认情况下，
双击托盘会把归档的卡片全翻回桌面上。**归档锁定**（设置项 `lockArchivedHidden`，默认开）补上这一环：

- 一旦发现有已归档卡片重新变成可见，插件在约 200 ms 后执行**一次合并扫描**，把它们重新隐藏；
- 合并是刻意的：一次"显示全部"会产生 N 个可见性事件，插件只读一次卡片列表、只隐藏需要隐藏的那几张；
- 从归档中心点「恢复」不受影响：恢复会先把记录标记为已恢复，锁定不会把它收回去；
- 关掉锁定后，被宿主翻出来的卡片会被视为"已恢复"（记录移入历史，右键菜单重新变成「归档此卡片」）。

代价：拦截发生在隐藏动作之后，所以会看到一帧闪现——这是"宿主没有过滤能力"换来的。

---

## 运行环境要求（重要）

这个插件声明 `"apiVersion": "2.2"`，并且依赖 **2.2 才有的能力**：

- `papers.hide` / `papers.show` 跨纸片显示控制 —— 「只从界面移除、不删数据」整个功能就建立在这上面；
- `startupPaper.presentation: "hidden"` —— 让归档中心在后台常驻但不占桌面。

因此它**只能在支持插件 API 2.2 的 PaperTodo 上运行**。已发布的 3.4 只支持到 2.1，
加载时会以 `Unsupported plugin API version 2.2` 直接拒绝，表现就是「装上去了但没有任何按钮」，
而且插件目录下不会生成 `.runtime`，也不会有 `plugins\data\qianshi.archive.native.json`。

判断宿主是否够新：看 `PaperTodo\CHANGELOG.md`，`Unreleased` 里写了
「Plugin API 2.2 and compatibility」的源码，才带 2.2。已知可用的宿主 = 用本工作区的
`PaperTodo\` 源码构建出来的 4.0.0-beta1。

用源码构建宿主（需要 .NET 10 SDK；`vendor/wpf-notifyicon` 子模块要先 `git submodule update --init --recursive`）：

```powershell
cd PaperTodo
dotnet build PaperTodo.csproj -c Release
# 产物：PaperTodo\输出\PaperTodo-v4.0.0-beta1\PaperTodo.exe
```

---

## 安装

需要 .NET 10 SDK，并且 `PaperTodo\` 在本仓库根目录下（插件编译时要引用宿主的契约工程）。

1. 完全退出 PaperTodo（原生插件一旦被加载就无法热替换）。
2. 在本仓库根目录运行：

```powershell
pwsh -ExecutionPolicy Bypass -File .\build\Build-And-Install-NativePlugin.ps1
```

脚本自动发现 `src\` 下所有插件，逐个 Release / win-x64 / 框架依赖发布，
剥离宿主已经提供的程序集和 PDB/XML，然后安装到 `<InstallTo>\plugins\<插件 id>\`，
并保留该目录下已有的 `.runtime\`。

只打某一个插件（其余保持不动）：

```powershell
pwsh -ExecutionPolicy Bypass -File .\build\Build-And-Install-NativePlugin.ps1 -Plugin QianShi.PaperTodo.Archive
```

装到源码构建出来的宿主（例如 `输出\PaperTodo-v4.0.0-beta1`）：

```powershell
pwsh -ExecutionPolicy Bypass -File .\build\Build-And-Install-NativePlugin.ps1 `
  -InstallTo 'PaperTodo\输出\PaperTodo-v4.0.0-beta1'
```

装到已安装的 PaperTodo（不是源码目录）时，同时指定源码位置和安装位置：

```powershell
pwsh -ExecutionPolicy Bypass -File .\build\Build-And-Install-NativePlugin.ps1 `
  -PaperTodoRoot 'D:\Client\Tools\PaperTodo' -InstallTo 'D:\Client\Tools\PaperTodo'
```

只打包、不安装（不需要关闭 PaperTodo）：

```powershell
pwsh -ExecutionPolicy Bypass -File .\build\Build-And-Install-NativePlugin.ps1 -SkipInstall
```

3. 启动 PaperTodo。插件清单里的 `startupPaper` 会创建一个隐藏的「归档中心」卡片，
   待办和笔记卡片的顶栏随即可见归档按钮。

> `-InstallTo` 默认等于 `-PaperTodoRoot`。从源码运行 PaperTodo 时，`PaperTodo.csproj` 会把
> `plugins\**` 复制到 `输出\<版本>\`，所以装到 `PaperTodo\plugins\` 后重新构建即可；
> 发布产物不捆绑插件，这一点与官方约定一致。

> **子模块洁净提示**：`PaperTodo` 现在是 git 子模块。日常部署建议直接用
> `-InstallTo 'PaperTodo\输出\<版本>'` 装到构建产物里，这样官方源码子模块保持干净、
> 不产生未跟踪文件。若确实要装进 `PaperTodo\plugins\`（让宿主编译时就带上插件），
> 它会在子模块里显示为未跟踪；本地可以用一条命令隐藏：
>
> ```powershell
> Add-Content PaperTodo\.git\info\exclude "`n/plugins/qianshi.archive.native/"
> ```

---

## 数据放在哪里

```text
PaperTodo\plugins\qianshi.archive.native\.runtime\archive-store.json
```

这是插件自有的文件，不属于 PaperTodo 的 `data.json`，也不受宿主「每张纸片 10 MiB / Runtime 20 MiB」
的状态限制。它保存：归档记录、回收站记录、已恢复历史，以及用于兜底的内容镜像。

写入方式是「写临时文件 → 原子替换」，读取失败时会把原文件复制成
`archive-store.json.corrupt-<时间戳>` 再继续，绝不会用一份空数据覆盖掉你的记录。
如果文件里的版本号比当前插件新（你回退了插件版本），插件会拒绝读写而不是降级破坏它。

---

## 能力边界（请先读这一段）

- **笔记里的图片无法恢复。** 宿主的插件 API 只提供图片读取，没有写入；卡片被真删除后，
  无引用的图片会被宿主回收。归档（隐藏）不受影响，图片一直在；只有「先被宿主删除、再从回收站重建」
  这条路径会丢掉图片，正文文字仍然完整。
- **正文由其它插件提供的笔记无法恢复正文。** 这类卡片没有可读可写的文本通道，
  插件会在记录上标记为不可恢复，而不是假装恢复成功。
- **镜像可能缺少最后几次输入。** 宿主的读取接口不会强制刷新编辑器里尚未同步的文本，
  所以「在 PaperTodo 里直接删除」这条路径找回的内容，可能是删除前的最后一次镜像。
  想完全避免这个风险，就用「归档」而不是删除。
- **插件只在运行时在线时保护你。** Runtime 的生命周期跟随插件自己的卡片：
  如果把「启动时保持归档服务运行」关掉，又没有打开归档中心，插件就不在运行，
  这段时间里宿主里发生的删除无法被记录。
- **「彻底删除」是真的删除。** 归档记录上的「彻底删除」会调用宿主的删除接口并丢弃快照，
  这是唯一会毁数据的操作，归档中心里一定会先要求确认。
  相对地，「归档」不做确认：它不删任何东西，随时可以一键恢复。

### 为什么笔记图片救不回来（已查证，别再重走）

宿主里「笔记」其实是两份数据：`data.json` 里的 Markdown 文本（图片行长成 `![image|100%](i:12345)`，
`i:` 后面只是 3–8 位数字编号），以及 exe 旁边的 `note-assets.lmdb`（真正的图片字节）。

三个事实决定了结论：

1. **回收是按"内容里有没有引用"判定的**：宿主启动时扫描**所有卡片**的 `Content`
   （`StateStore.TryAddImageReferences`，不限 Type、不限 provider），把没有被引用的 blob 删掉，
   并把编号还回复用池。所以「把 `i:xxxxx` 写进另一张 Markdown 卡片当保险箱」**确实能让图片不被回收**，
   也能避免编号被复用成别的图。
2. **但图片资产带主人**：每个 blob 记录 `NoteId`，编辑器渲染时必须 `asset.NoteId == 当前卡片 id`
   （`MarkdownTextBox.SemanticImages.cs`），而宿主**没有"加载时重新认领"的逻辑**。
   恢复会重建一张**新 id** 的卡片 ⇒ 归属对不上 ⇒ 图片位置只能是缺失/占位。
   **所以"保险箱"是必要条件，不是充分条件。**
3. **插件也读不到"事后"的字节**：`ReadImage` 同样校验 `asset.NoteId == paperId`，
   卡片一删就连读都失败。要留住字节，**只能在卡片还在的那一刻读出来**，不能指望删除后补救。

结论：插件侧能做的上限是「保住字节 + 保住编号」，救不回卡片内的显示。要彻底解决只有一条路——
宿主给插件一个写接口（内部已有 `NoteImageStore.AddImages(noteId, …)` 这条完整路径，
只差一个 `WriteImage(paperId, mime, bytes) -> imageId` 的公开契约与对应权限，属于 API 2.3 级别的改动）。
有了它，插件捕获时读字节、恢复时重新导入并改写文本里的 `i:旧id`，图片即可完整内嵌恢复；
届时上面的"保险箱"思路也就不再需要了。

---

## 权限

插件清单声明了这些 Workspace 权限，全部是功能必需的：

| 权限 | 用途 |
| --- | --- |
| `papers.read` / `papers.observe` | 读取卡片列表与变更，镜像内容 |
| `papers.create` | 从快照重建被删除的卡片 |
| `papers.delete` | 「彻底删除」已归档的卡片 |
| `todos.read` / `todos.observe` | 读取与镜像待办条目 |
| `todos.append` | 恢复被删除的待办条目 |
| `todos.update` | 重建时写回条目的完成状态与提醒 |
| `todos.delete` | 归档单条待办（先快照再删除） |
| `notes.read` / `notes.observe` | 读取与镜像笔记正文 |
| `notes.append` / `notes.replace` | 重建笔记正文 |

PaperTodo 不对插件提供安全沙箱，插件是完全可信代码，只应从可信来源安装。

---

## 验证

插件自带一个离线、无需宿主的检查程序（与仓库 `tests/` 下的检查程序同一套路子）：

```powershell
dotnet run --project .\tests\QianShi.PaperTodo.Archive.Checks
```

它用一个内存假宿主（记录每一次 create / append / delete / hide / show 调用）跑 35 项检查：
存储往返、损坏文件保留、保留策略、归档不删数据、恢复、彻底删除、权限与外来正文，
Provider Runtime 本身——顶栏/右键菜单注册、禁用设置时不注册、顶栏归档只隐藏不删除、
右键取消归档、宿主删除捕获镜像正文、Dispose 清理、启动时宿主读取失败不拖垮 Runtime，
以及归档锁定——"显示全部"风暴只触发一次合并扫描、恢复不被收回、恢复失败回滚、Dispose 取消待执行扫描。
当前结果：`ARCHIVE_CHECKS_RESULT 35/35 passed; 0 failed`。

---

## 开发

```powershell
# 整个解决方案（插件 + 检查程序）
dotnet build .\QianShi.PaperTodo.Plugins.slnx -c Release

# 只编译插件
dotnet build .\src\QianShi.PaperTodo.Archive\QianShi.PaperTodo.Archive.csproj -c Release
```

插件面向 PaperTodo Plugin API `2.2`（`plugin.json` 的 `apiVersion`），
类型契约在 `PaperTodo\PaperTodo.Plugin.Abstractions\`，插件开发手册在
`PaperTodo\plugin-samples\README.md`。

改完插件文件后必须重启 PaperTodo：原生插件不支持热重载。
