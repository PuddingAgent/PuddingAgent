# PuddingFinance.Documents
> 源指纹: Source/PuddingFinance.Documents/**=3-0f2c7c24ba93 · 条目数: 2 · 最近整理: 2026-11-22

财务文档的 PDF 文本抽取组件（net10.0，引 PdfPig）。对外只暴露自有契约，第三方 PDF 库类型不外泄，升级或换库时改动收敛在一个文件里。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `PdfTextAbstractions.cs` | PDF 文本抽取的契约与结果度量 | `IPdfTextSource`、`PdfDocumentText`、`PdfPageText` | `PdfPigTextSource.cs` | 零字符必须显式暴露，它意味着扫描件要上 OCR 而不是返回空文档 |
| `PdfPigTextSource.cs` | 基于 PdfPig 的按阅读顺序抽取实现 | `PdfPigTextSource`、`CountCjk` | `PdfTextAbstractions.cs` | 全仓只有这里引用 PdfPig，不得从其他文件直接用其类型 |
