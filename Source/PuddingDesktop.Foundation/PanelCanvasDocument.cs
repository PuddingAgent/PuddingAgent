namespace PuddingDesktop.Foundation;

/// <summary>
/// 画布内容的两种包装：**嵌入宿主**（沙箱）与**导出**（独立文件）。
///
/// 为什么必须分成两种：CSP 是**容器约束**，不是内容的属性。
/// 嵌进宿主时要收紧——内容不该联网，也拿不到任何 native 桥；
/// 但导出成独立文件时若继续带着它，用户在自己机器上打开会被静默拦住自己加的资源，
/// 而页面上不会出现任何提示。
///
/// 纯字符串拼装，因此可以在无窗口环境测；Shell 只负责把结果交给 WebView2 / 写盘。
/// </summary>
public static class PanelCanvasDocument
{
    /// <summary>
    /// 嵌入宿主时的 CSP：断网、禁一切外部资源，只放开内联样式/脚本（画布交互靠内联 JS）
    /// 与两类图片：<c>data:</c>（内嵌小块内容）和自定义 <c>pudding-image:</c>
    /// （Shell 拦截后读盘，见 <see cref="PanelImageSource"/>）。
    /// 后者不可省：4K 图片走 base64 会让画布卡顿，且标注要反复回写整份内容。
    /// </summary>
    public const string EmbeddingContentSecurityPolicy =
        "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; "
        + "img-src data: " + PanelImageSource.ContentSecurityPolicySource + "; font-src data:;";

    /// <summary>
    /// CSS 兜底：内容只写 <c>var(--pudding-*)</c> 即可，不需要自己猜主题。
    /// 变量缺失时（例如样式表没注入）仍有 <c>--pudding-page</c> 的 fallback。
    /// </summary>
    private const string BaseCss =
        "html,body{margin:0;background:var(--pudding-page);"
        + "font-family:'Segoe UI','Microsoft YaHei',sans-serif;}";

    /// <summary>
    /// 包成嵌入宿主的文档：带沙箱 CSP 与宿主主题变量。
    /// <paramref name="content"/> 是**片段**（<c>&lt;style&gt;</c>/<c>&lt;div&gt;</c>/<c>&lt;svg&gt;</c>…），
    /// 原样插入 body，**不做 HTML 转义** —— 它本来就是 HTML。
    /// </summary>
    public static string WrapForEmbedding(string content, WorkbenchColorScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Header(PanelCanvasTheme.Declare(scheme), title: null, csp: EmbeddingContentSecurityPolicy)
            + content + AnnotationScript + "</body></html>";
    }

    /// <summary>
    /// 包成可独立打开的文档：**不带 CSP**（那是容器约束），但保留主题变量，
    /// 这样在浏览器里打开时配色仍然正确。
    /// </summary>
    public static string WrapForExport(string content, WorkbenchColorScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Header(PanelCanvasTheme.Declare(scheme), title: "Pudding 画布", csp: null)
            + content + "</body></html>";
    }

    /// <summary>
    /// 把标注层注入**嵌入版**文档（不注入导出文件：导出的是内容快照，不需要一层交互壳）。
    ///
    /// <para>
    /// 为什么放在壳里而不是让 Agent 自己写：这是基础设施，不是每张图各写一遍的东西。
    /// 而且标注层要按“最大的那张图”定位，只有壳知道页面里到底放了什么。
    /// </para>
    /// </summary>
    private const string AnnotationScript = "<script>" + AnnotationScriptSource + "</script>";

    /// <summary>
    /// 标注层：默认隐藏，由 XAML 工具栏的「标注」按钮切换。
    ///
    /// <para>
    /// **页面只报像素**（相对图片左上角）与图片尺寸，归一化留给 C# 的
    /// <see cref="PanelAnnotation.FromPixels"/>。换算只保留一份实现：两份（JS + C#）
    /// 必然漂移，而漂移后的坐标只会生成一张“改错地方”的图，用户还以为模型不听话。
    /// </para>
    ///
    /// <para>
    /// 不向宿主推消息、不开任何桥：XAML 侧用 <c>ExecuteScriptAsync</c> 取
    /// <c>puddingAnnotation.take()</c> 的返回值。只有用户点了工具栏按钮才会读取一次。
    /// </para>
    /// </summary>
    private const string AnnotationScriptSource = """
        (function () {
          if (window.puddingAnnotation) return;
          var items = [], layer = null, active = false, box = null, start = null, imageEl = null;
          // layer 是复用的（ensureLayer 只建一次），bind 必须幂等：
          // 否则「开→关→再开」会重复绑事件，一次拖拽落两个标注。
          var bound = false;

          function biggestImage() {
            var best = null, area = 0, list = document.images;
            for (var i = 0; i < list.length; i++) {
              var im = list[i];
              if (!im.complete || !im.naturalWidth || !im.naturalHeight) continue;
              var r = im.getBoundingClientRect();
              var a = r.width * r.height;
              if (a > area) { area = a; best = im; }
            }
            return best;
          }

          function ensureLayer() {
            if (!layer) {
              layer = document.createElement('div');
              layer.setAttribute('data-pudding-annotation', '');
              document.body.appendChild(layer);
            }
            return layer;
          }

          function sync() {
            box = null;
            var im = biggestImage();
            if (!im) return false;
            var r = im.getBoundingClientRect();
            if (r.width <= 0 || r.height <= 0) return false;
            imageEl = im;
            box = r;
            var l = ensureLayer();
            l.style.cssText = ['position:fixed','z-index:2147483647','cursor:crosshair',
              'box-sizing:border-box','background:rgba(59,111,168,.06)',
              'border:1px dashed rgba(59,111,168,.85)',
              'left:' + r.left + 'px','top:' + r.top + 'px',
              'width:' + r.width + 'px','height:' + r.height + 'px',
              'display:' + (active ? 'block' : 'none')].join(';');
            paint();
            return true;
          }

          function paint() {
            if (!layer) return;
            layer.innerHTML = '';
            items.forEach(function (it) {
              var d = document.createElement('div');
              var s = ['position:absolute','pointer-events:none',
                'border:1.5px solid #3B6FA8','background:rgba(59,111,168,.14)'];
              if (it.kind === 'box') {
                s.push('left:' + Math.min(it.x1, it.x2) + 'px');
                s.push('top:' + Math.min(it.y1, it.y2) + 'px');
                s.push('width:' + Math.abs(it.x2 - it.x1) + 'px');
                s.push('height:' + Math.abs(it.y2 - it.y1) + 'px');
              } else {
                s.push('left:' + (it.x - 6) + 'px');
                s.push('top:' + (it.y - 6) + 'px');
                s.push('width:12px'); s.push('height:12px'); s.push('border-radius:50%');
              }
              d.style.cssText = s.join(';');
              layer.appendChild(d);
            });
          }

          function local(e) {
            var b = layer.getBoundingClientRect();
            return { x: e.clientX - b.left, y: e.clientY - b.top };
          }

          function onDown(e) {
            if (!active || !box) return;
            start = local(e);
            layer.setPointerCapture(e.pointerId);
            e.preventDefault();
          }

          function onUp(e) {
            if (!active || !start) return;
            var end = local(e);
            var dx = Math.abs(end.x - start.x), dy = Math.abs(end.y - start.y);
            // 小于 4px 当成点选：手拖框很容易变成一个 2px 的碎框，那种框交给模型没意义。
            if (dx < 4 && dy < 4) {
              items.push({ kind: 'point', x: end.x, y: end.y });
            } else {
              items.push({ kind: 'box', x1: start.x, y1: start.y, x2: end.x, y2: end.y });
            }
            start = null;
            paint();
          }

          function bind(l) {
            if (bound) return;
            bound = true;
            l.addEventListener('pointerdown', onDown);
            l.addEventListener('pointerup', onUp);
            // 拖到图片外面松手也要落一个框，否则越界的拖拽会默默消失。
            document.addEventListener('pointerup', onUp);
          }

          window.puddingAnnotation = {
            enable: function () { active = true; if (sync()) bind(layer); return !!box; },
            disable: function () { active = false; start = null; if (sync()) layer.style.display = 'none'; },
            toggle: function () {
              if (active) { this.disable(); return false; }
              return this.enable();
            },
            clear: function () { items = []; if (sync()) paint(); },
            count: function () { return items.length; },
            take: function () {
              if (!sync()) return '';
              return JSON.stringify({
                src: (imageEl && imageEl.src) || '',
                width: box.width, height: box.height,
                items: items.map(function (it) {
                  return it.kind === 'box'
                    ? { kind: 'box', x1: it.x1, y1: it.y1, x2: it.x2, y2: it.y2 }
                    : { kind: 'point', x: it.x, y: it.y };
                })
              });
            }
          };
        })();
        """;

    private static string Header(string themeCss, string? title, string? csp)
    {
        var head = "<!doctype html><html><head><meta charset=\"utf-8\">";
        if (csp is not null)
        {
            head += "<meta http-equiv=\"Content-Security-Policy\" content=\"" + csp + "\">";
        }

        if (title is not null)
        {
            head += "<title>" + title + "</title>";
        }

        return head + "<style>" + themeCss + BaseCss + "</style></head><body>";
    }
}
