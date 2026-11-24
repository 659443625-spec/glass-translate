离线词库 dict.bin 生成说明
================================

为什么仓库里没有 dict.bin / ecdict.csv
----------------------------------------
- 源词库 app/data/ecdict.csv 约 63 MB（ECDICT 全量 CSV），体积过大，不进 git。
- 生成物 app/assets/dict.bin 约 8.8 MB，也不进 git；它以 GitHub Release 附件的形式分发，
  文件名：dict.bin。

数据来源与许可
--------------
ECDICT（https://github.com/skywind3000/ECDICT），MIT License。
本项目的离线词库是 ECDICT 的精简派生数据，保留其 MIT 许可与署名。

自己生成 dict.bin 的步骤
------------------------
前提：Windows 10/11，系统自带 .NET Framework 4.x 的 csc 编译器，无需安装 SDK。

1) 下载 ECDICT 全量 CSV，放到 app/data/ecdict.csv
   下载地址：https://github.com/skywind3000/ECDICT
   文件名一般为 ecdict.csv（约 63 MB）。
   本机校验值（SHA-256）：
   D1ECBDF9EE2B920AFB6F430CA6B6BB9F559588CF0DFF86E04BD255F9390843CC

2) 编译生成工具 app/tools/make-dict.cs（在仓库根目录执行）
   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:exe ^
     /out:app\tools\make-dict.exe app\tools\make-dict.cs

3) 生成 app/assets/dict.bin
   app\tools\make-dict.exe app\data\ecdict.csv app\assets\dict.bin

   正常的输出形如：
     总行数 770611，保留 6xxxx，跳过 7xxxxx
     去重后 57484 条
     写出 app\assets\dict.bin  8.8 MB
   （词条数 57484 为已发布 dict.bin 的实际值；行数为 ECDICT 全量版本量级，会随上游更新浮动）

   本仓库发布用 dict.bin 的校验值（SHA-256）：
   684F0733A23EC6FFCBFB91CA6A7AA72526FF5451BF4EBB34CF05B5932D8C50B4
   字节数：9189830（约 8.8 MB）

4) 构建时自动打包
   app\build.ps1 会把 app\assets\dict.bin 复制到 app\dist\dict.bin。
   发布包 MouseTranslate-v1.0.0-win64.zip 内已内置 dict.bin，最终用户无需任何操作。

精简规则（见 make-dict.cs）
---------------------------
- 只保留：带考试标签（zk/gk/cet4/cet6/ky/toefl/ielts）或词频/BNC 排名前 50000 的英文词条；
- 去重（同一词取排名靠前者），按字母序排序；
- 输出格式 MTD1：magic "MTD1" + 词条数 + 词串区偏移 + 词条区偏移，后接索引、词串、词条；
- 词条含：音标、中文释义、英文释义、标签。

提示
----
- 没有 dict.bin 时程序仍可联网翻译，只是离线查词不可用。
- 首次运行查词顺序：在线词典 → 离线词库 → 机器翻译（详见使用说明.md）。
