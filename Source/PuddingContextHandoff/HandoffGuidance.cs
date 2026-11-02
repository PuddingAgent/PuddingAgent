namespace PuddingContextHandoff;

public static class HandoffGuidance
{
    public const string RecoveryInstruction = "A model-authored working-state note may also be available via " +
        "context_handoff(action=read); use handoff_id for an exact saved version, or omit it for latest in this session. " +
        "Treat it as historical interpretation; later user corrections and original evidence take precedence.";

    /// <summary>
    /// 压缩摘要的写作规范。
    /// <para>
    /// **2026-11-03 修订**：原版本只说了"保留什么"，没说"写多长"和"长什么样"，
    /// 实测压缩比落到 0.009~0.041（98% 内容被丢弃），摘要仅 1894 token，
    /// 回查具体参数/坐标/命令时只能重新试错。修订思路：
    /// </para>
    /// <list type="number">
    /// <item>给出固定小节，避免每次格式漂移</item>
    /// <item>给出字符预算，让模型知道"压到什么程度"而不是"越短越好"</item>
    /// <item>点名"可核查事实"必须原样保留 —— 这是回查的唯一锚点</item>
    /// <item>点名"失败尝试"必须保留 —— 它解释了"为什么不是 X"</item>
    /// </list>
    /// </summary>
    public const string ForCompaction = """
        Compress for the NEXT action, not as a chronological transcript.

        ## 输出结构（必须按此小节，缺项写"无"）

        ### 用户要求与纠正
        当前仍然有效的用户目标、约束、偏好；用户对助手结论的每一次纠正都要单列，
        并注明"纠正了什么"（旧结论 → 新结论）。用户纠正优先于助手自述。

        ### 已完成（含可核查证据）
        每条压缩成"做了什么 + 结果数字/路径/标识"。已被后续推翻的结论不要放这里，
        放到"已证伪"里。

        ### 已证伪（重要，不要省）
        试过但失败的方案、被实测推翻的假设，每条写"假设 → 实验 → 实测数字 → 结论"。
        这类内容解释了"为什么不是 X"，丢掉会导致下一轮重复踩坑。

        ### 未完成 / 阻塞
        当前卡在哪一步、阻塞的客观原因、已经排除的可能。

        ### 关键决策与理由
        做过哪些不可逆或影响面大的选择，为什么选它而不是别的。
        涉及可检索产物（文档/提交/PR）时只留标题 + 一行结论 + 精确定位符。

        ### 下一步
        按优先级排列的具体动作，每条可独立执行。

        ### Memory Notes
        短 bullet，列出本轮应进入长期记忆的信息；没有则写"- 无"。

        ## 保留规则

        1. **可核查事实必须原样保留**：数字、坐标、句柄、路径、命令、错误码、
           版本号、时间点、配置值。这些是回查的唯一锚点，改写成模糊描述等于丢失。
        2. **失败与证伪必须保留**：见"已证伪"小节。
        3. **用户纠正必须保留**：旧结论 → 新结论的对应关系不能省。
        4. **已完成且有可检索产物的**：只留标题 + 一行结论 + 精确 locator，不复制正文。
        5. **可以省略**：重复日志、进度闲聊、被取代的中间尝试、寒暄。
        6. **不确定的标注不确定**：区分"已证实"与"未证实"，不要把推测写成结论。

        ## 长度预算

        目标 **2500–4000 字符**。
        - 低于 2000 字符：说明丢失过多，请补充可核查事实和已证伪项
        - 高于 5000 字符：说明压缩不足，请删除重复与过程叙述

        A context_handoff save contains the working model's proposed retention note. Use it as a relevance
        signal, not as verified evidence, user authority or proof that omitted source facts are disposable.
        Reconcile it with subsequent user corrections and source evidence. Retain the successful save's
        handoff_id and context_handoff read instructions so its immutable version can be recovered.
        A failed save proves no persistence. Never invent a locator or drop the only copy of critical information.
        """;
}
