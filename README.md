升级：

可以选择几个元素，共享给别人或者团队
输入共享码添加进来，该控件所有人可以操作（可以只读）

一个人可以有多个团队，不会切换到具体团队，也就是说任何一个桌面可以放不同团队共享过来的模块


https://pan.baidu.com/union/home


# 版本号由 csproj 按构建时间自动生成（FileVersion = 1.0.<距2025-01-01天数>.<HHmm>），packVersion 取前三段（SemVer，粒度到天）：
$exe = "F:\_WORK\_SIDEDESK\sidedesk-win-exe\bin\Release\net10.0-windows10.0.19041\SideDesk.exe"
$vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
$packVer = "$($vi.FileMajorPart).$($vi.FileMinorPart).$($vi.FileBuildPart)"
vpk pack --packId com.desktxp.sidedesk --packDir F:\_WORK\_SIDEDESK\sidedesk-win-exe\bin\Release\net10.0-windows10.0.19041 --mainExe SideDesk.exe --outputDir F:\_WORK\_SIDEDESK\sidedesk-win-exe\bin\updates --signParams "/sha1 c2ce45210f4a17db124fae5429de4dcbee28b81d /tr http://timestamp.sectigo.com?td=sha256 /td sha256 /fd sha256" --shortcuts None  --exclude ".*\.pdb$|.*/logs/.*|.*\.xml$" --packVersion 1.0.8