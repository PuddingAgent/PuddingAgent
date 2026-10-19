import { lazy, type ComponentType, type LazyExoticComponent } from 'react';

/**
 * 富媒体围栏注册表：` ```<language> ` → 懒加载渲染器。
 *
 * ## 为什么要有这层间接
 *
 * 体积是硬约束：chat 路由 chunk 门禁 ≤ 496 KiB，而单个可视化库就是百 KB 级
 * （mermaid 主入口 173.5 KB gzip、ECharts 全量 368 KB gzip）。所以**识别**与**加载**
 * 必须分开：识别是同步查表（几字节），加载是 `React.lazy` + 动态 import（首屏零成本）。
 *
 * ## 为什么用声明式围栏而不是任意 HTML
 *
 * 模型只产出**数据**（DSL / JSON），渲染器由我们控制。这样 XSS 面停留在
 * MarkdownBlock 已有的那份白名单上，不会因为新增组件而扩大。
 *
 * 新增一类组件 = 在这里加一行；不需要动 Markdown 管线，也不需要新协议。
 */
export interface RichBlockProps {
  /** 围栏内的原始文本（Mermaid DSL / JSON spec 等）。 */
  code: string;
  /**
   * artifact → rows 的解析器，由宿主注入。
   * 本层不读任何存储：读取受工作区边界约束，应该发生在宿主侧。
   * 未注入时，引用 artifact 的图表会显式提示，而不是画空图。
   */
  resolveArtifactRows?: (artifactId: string) => Array<Record<string, unknown>> | null;
}

export type RichBlockComponent = LazyExoticComponent<ComponentType<RichBlockProps>>;

const RICH_BLOCKS: Record<string, RichBlockComponent> = {
  mermaid: lazy(() => import('./MermaidBlock')),
  chart: lazy(() => import('./ChartBlock')),
};

/** 已注册的围栏语言（用于文档、测试与排障）。 */
export const RICH_BLOCK_LANGUAGES = Object.keys(RICH_BLOCKS);

/**
 * 按围栏语言解析渲染器；未注册返回 undefined（调用方应回退为普通代码块）。
 * 大小写不敏感：模型常写 `Mermaid` / `MERMAID`。
 */
export function resolveRichBlock(language?: string | null): RichBlockComponent | undefined {
  if (!language) return undefined;
  return RICH_BLOCKS[language.trim().toLowerCase()];
}

export function isRichBlockLanguage(language?: string | null): boolean {
  return resolveRichBlock(language) !== undefined;
}
