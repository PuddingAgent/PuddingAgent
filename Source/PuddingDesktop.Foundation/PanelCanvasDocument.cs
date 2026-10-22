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
    /// <remarks>
    /// 全局 <c>border-box</c> 不是风格偏好，是**实测过的缺陷修复**：画布上的
    /// <c>img</c> 只要写成 <c>width:100%</c> 再配一条 1px 边框，content-box 下
    /// 就比容器宽 2px，画布底部立刻出现横向滚动条——而内容看起来完全正常，
    /// 没人会想到是盒模型。与其要求每份 Agent 内容自己记得加，不如在壳里统一。
    /// </remarks>
    private const string BaseCss =
        "*,*::before,*::after{box-sizing:border-box;}"
        + "html,body{margin:0;background:var(--pudding-page);"
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

          // 当前工具。模型协议只有 point/bbox，其余都是「降维」：
          // 涂鸦 → bbox + 轨迹（轨迹留给烧录）、箭头 → 两个 point、文字 → point + 原文。
          var tool = 'box';
          var draft = null;      // 拖拽中的轨迹（涂鸦笔 / 箭头）
          var undoStack = [];    // 每步操作前的快照；容量 50

          // 每个工具一个光标。不区分的话整层都是十字星，用户看不出「现在拿的是笔还是框」。
          // 涂鸦用自定义 SVG 画笔：内置光标里没有笔，而 encodeURIComponent 保证
          // data: 里只出现合法字符（引号、尖括号、# 全部转义）。浏览器不认时退回十字星。
          var PEN_CURSOR = 'url("data:image/svg+xml;charset=utf-8,'
            + encodeURIComponent(
              '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24">'
              + '<path d="M2 22l1-4L16.4 4.6a2 2 0 0 1 2.8 0l.2.2a2 2 0 0 1 0 2.8L6 21z" '
              + 'fill="#E4572E" stroke="#FFFFFF" stroke-width="1.3" stroke-linejoin="round"/></svg>')
            + '") 2 22, crosshair';

          var CURSORS = {
            box: 'crosshair',
            point: 'crosshair',
            arrow: 'crosshair',
            text: 'text',
            pen: PEN_CURSOR
          };

          function applyCursor() {
            if (layer) layer.style.cursor = CURSORS[tool] || 'crosshair';
          }

          function pushUndo() {
            undoStack.push(JSON.stringify(items));
            if (undoStack.length > 50) undoStack.shift();
          }

          function undo() {
            if (!undoStack.length) return false;
            items = JSON.parse(undoStack.pop());
            if (sync()) paint();
            return true;
          }

          // 涂鸦与箭头用 SVG 画：div 只能画轴对齐矩形，画不出斜线与任意轨迹。
          // SVG 层与 div 层同为 layer 的子元素，坐标系一致，坐标可以直接复用。
          function drawVector(svg, it) {
            var ns = 'http://www.w3.org/2000/svg';
            var stroke = '#E4572E';

            if (it.kind === 'scribble') {
              var pts = it.points || [];
              if (!pts.length) return;
              var poly = document.createElementNS(ns, 'polyline');
              poly.setAttribute(
                'points', pts.map(function (p) { return p.x + ',' + p.y; }).join(' '));
              poly.setAttribute('fill', 'none');
              poly.setAttribute('stroke', stroke);
              poly.setAttribute('stroke-width', '3');
              poly.setAttribute('stroke-linecap', 'round');
              poly.setAttribute('stroke-linejoin', 'round');
              svg.appendChild(poly);
              return;
            }

            var line = document.createElementNS(ns, 'line');
            line.setAttribute('x1', it.x1);
            line.setAttribute('y1', it.y1);
            line.setAttribute('x2', it.x2);
            line.setAttribute('y2', it.y2);
            line.setAttribute('stroke', stroke);
            line.setAttribute('stroke-width', '3');
            line.setAttribute('stroke-linecap', 'round');
            svg.appendChild(line);

            // 箭头头部按方向旋转，不用 <marker>：marker 需要 defs + id，
            // 而这里每次重绘都重建整层，带 id 的引用很容易悬空。
            var angle = Math.atan2(it.y2 - it.y1, it.x2 - it.x1);
            var len = 14;
            var head = document.createElementNS(ns, 'polygon');
            head.setAttribute('points', [
              [it.x2, it.y2],
              [it.x2 - len * Math.cos(angle - 0.45), it.y2 - len * Math.sin(angle - 0.45)],
              [it.x2 - len * Math.cos(angle + 0.45), it.y2 - len * Math.sin(angle + 0.45)]
            ].map(function (p) { return p[0] + ',' + p[1]; }).join(' '));
            head.setAttribute('fill', stroke);
            svg.appendChild(head);
          }

          // 文字工具：点击位置后弹输入框。不能用 window.prompt ——
          // WebView2 默认把原生对话框拦下（ScriptDialogOpening），点了没反应。
          function askText(x, y) {
            // 输入框挂在**图层**上并沿用图层坐标。早前挂在 body 上却传图层坐标
            // （已减去图片左上角），输入框会跑到页面左上角 —— 用户看到的是
            // 「点在这儿、框在别处」，输入的字也落在错的位置。
            var inputWidth = 250;
            var limitX = box ? Math.max(0, box.width - inputWidth - 6) : 0;
            var left = Math.min(Math.max(0, x), limitX);
            var top = y + 8;
            // 靠近下边缘时翻到点击点上方，别让输入框掉出图片。
            if (box && top + 32 > box.height) top = Math.max(0, y - 36);

            var wrap = document.createElement('div');
            wrap.setAttribute('data-pudding-text-input', '');
            wrap.style.cssText = 'position:absolute;left:' + left + 'px;top:' + top + 'px;'
              + 'z-index:2147483647;pointer-events:auto';
            var input = document.createElement('input');
            input.type = 'text';
            input.placeholder = '要改什么？（Enter 确认，Esc 取消）';
            input.style.cssText = 'width:' + inputWidth + 'px;padding:5px 8px;'
              + 'border:1px solid #E4572E;border-radius:6px;font-size:12px;'
              + 'background:#fff;color:#222;box-shadow:0 2px 8px rgba(0,0,0,.18)';
            wrap.appendChild(input);
            ensureLayer().appendChild(wrap);
            input.focus();

            function discard() {
              if (!wrap.isConnected) return;
              wrap.remove();
              undoStack.pop();
            }

            function commit() {
              if (!wrap.isConnected) return;
              var value = (input.value || '').trim();
              if (!value) { discard(); return; }
              wrap.remove();
              // 落点用输入框左上角：**在哪儿输入，字就出现在哪儿**。
              // 屏幕位置与 take() 报给模型的位置必须是同一个。
              items.push({ kind: 'text', label: value, x: left, y: top });
              paint();
            }

            input.addEventListener('keydown', function (ev) {
              ev.stopPropagation();
              if (ev.key === 'Enter') { commit(); }
              else if (ev.key === 'Escape') { discard(); }
            });
            input.addEventListener('blur', commit);
          }

          // 合成导出：把原图与所有标记画进 canvas。
          // **形式 1（任意标记）的前提就是这个**：笔迹必须落在像素里，
          // 否则发给模型的仍是干净原图，用户画了等于没画。
          function compose() {
            if (!imageEl || !imageEl.complete) return '';
            var w = imageEl.naturalWidth, h = imageEl.naturalHeight;
            if (!w || !h) return '';
            var rect = imageEl.getBoundingClientRect();
            if (!rect.width || !rect.height) return '';

            // 结果要经 ExecuteScriptAsync 回传宿主，而那条通道对返回值有大小上限；
            // 2K PNG 的 base64 常有 3~8MB，会静默拿不回来。
            // 所以限制最长边并改用 JPEG：体积降到几百 KB，
            // 而等比缩放不影响标记的相对位置（坐标仍按原图比例换算）。
            var maxEdge = 1536;
            var shrink = Math.min(1, maxEdge / Math.max(w, h));
            var outW = Math.max(1, Math.round(w * shrink));
            var outH = Math.max(1, Math.round(h * shrink));
            var sx = outW / rect.width, sy = outH / rect.height;

            var canvas = document.createElement('canvas');
            canvas.width = outW;
            canvas.height = outH;
            var ctx = canvas.getContext('2d');
            if (!ctx) return '';
            ctx.drawImage(imageEl, 0, 0, outW, outH);
            ctx.lineWidth = Math.max(3, Math.round(outW / 300));
            ctx.lineCap = 'round';
            ctx.lineJoin = 'round';
            ctx.strokeStyle = '#E4572E';
            ctx.fillStyle = '#E4572E';

            items.forEach(function (it) {
              if (it.kind === 'box') {
                ctx.strokeRect(
                  it.x1 * sx, it.y1 * sy, (it.x2 - it.x1) * sx, (it.y2 - it.y1) * sy);
              } else if (it.kind === 'scribble') {
                var pts = it.points || [];
                if (!pts.length) return;
                ctx.beginPath();
                pts.forEach(function (p, i) {
                  var px = p.x * sx, py = p.y * sy;
                  if (i === 0) { ctx.moveTo(px, py); } else { ctx.lineTo(px, py); }
                });
                ctx.stroke();
              } else if (it.kind === 'arrow') {
                var x1 = it.x1 * sx, y1 = it.y1 * sy, x2 = it.x2 * sx, y2 = it.y2 * sy;
                ctx.beginPath();
                ctx.moveTo(x1, y1);
                ctx.lineTo(x2, y2);
                ctx.stroke();
                var angle = Math.atan2(y2 - y1, x2 - x1), len = Math.max(12, outW / 60);
                ctx.beginPath();
                ctx.moveTo(x2, y2);
                ctx.lineTo(x2 - len * Math.cos(angle - 0.45), y2 - len * Math.sin(angle - 0.45));
                ctx.lineTo(x2 - len * Math.cos(angle + 0.45), y2 - len * Math.sin(angle + 0.45));
                ctx.closePath();
                ctx.fill();
              } else if (it.kind === 'text' && it.label) {
                // 与屏幕对齐：左上角锚点 + textBaseline:'top'。字号按缩放比例走 ——
                // 屏幕上 12px 的字，烧到 2048px 的图上必须跟着放大，否则模型看不清。
                var fontPx = Math.max(12, Math.round(12 * sx));
                ctx.font = fontPx + 'px sans-serif';
                ctx.textBaseline = 'top';
                var tx = it.x * sx, ty = it.y * sy;
                var textW = ctx.measureText(it.label).width;
                // 白底：直接往照片上写字，字色与画面同色时会看不见。
                ctx.fillStyle = 'rgba(255,255,255,.86)';
                ctx.fillRect(tx - 3, ty - 2, textW + 6, Math.round(fontPx * 1.4) + 4);
                ctx.fillStyle = '#B23A16';
                ctx.fillText(it.label, tx, ty);
                ctx.textBaseline = 'alphabetic';
                ctx.fillStyle = '#E4572E';
              }
            });

            // JPEG 而非 PNG：同样内容体积约小一个数量级，更适合过脚本通道。
            try { return canvas.toDataURL('image/jpeg', 0.9); } catch (err) { return ''; }
          }

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

          // 外部把「已存的标记」灌回来（制成品「继续编辑」走这条路）。
          // 传的是 **归一化 0–1000**（与 C# PanelAnnotation.Normalize 同一套除数），
          // 这里按当前显示尺寸换算成像素 —— 图元自身仍存像素，与其它路径一致。
          // 图还没布局好时返回 0，由调用方重试；不抛异常。
          function loadFromPayload(list) {
            if (!list || !list.length) return 0;
            if (!sync()) return 0;
            var w = box.width, h = box.height;
            function nx(n) { return (n || 0) / 1000 * w; }
            function ny(n) { return (n || 0) / 1000 * h; }

            var next = [];
            list.forEach(function (it) {
              if (!it) return;
              if (it.kind === 'box' || it.kind === 'arrow') {
                next.push({
                  kind: it.kind, x1: nx(it.x1), y1: ny(it.y1), x2: nx(it.x2), y2: ny(it.y2)
                });
              } else if (it.kind === 'point') {
                next.push({ kind: 'point', x: nx(it.x), y: ny(it.y) });
              } else if (it.kind === 'text') {
                next.push({ kind: 'text', label: it.label, x: nx(it.x), y: ny(it.y) });
              } else if (it.kind === 'scribble') {
                var pts = (it.points || []).map(function (p) {
                  return { x: nx(p.x), y: ny(p.y) };
                });
                if (!pts.length) return;
                next.push({ kind: 'scribble', points: pts });
              }
            });

            if (!next.length) return 0;
            pushUndo();
            items = next;
            paint();
            return next.length;
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
            l.style.cssText = ['position:fixed','z-index:2147483647',
              'box-sizing:border-box','background:rgba(59,111,168,.06)',
              'border:1px dashed rgba(59,111,168,.85)',
              'left:' + r.left + 'px','top:' + r.top + 'px',
              'width:' + r.width + 'px','height:' + r.height + 'px',
              'display:' + (active ? 'block' : 'none')].join(';');
            // cssText 整段赋值会清掉内联 cursor，必须在这之后按当前工具重设。
            applyCursor();
            paint();
            return true;
          }

          function paint() {
            if (!layer) return;
            layer.innerHTML = '';
            var svg = null;

            items.forEach(function (it, index) {
              // 向量图元（涂鸦/箭头）画进同一个 SVG 层：div 只能画轴对齐矩形。
              if (it.kind === 'arrow' || it.kind === 'scribble') {
                if (!svg) {
                  svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
                  svg.setAttribute('data-pudding-vector', '');
                  svg.style.cssText = 'position:absolute;left:0;top:0;width:100%;'
                    + 'height:100%;overflow:visible;pointer-events:none';
                  layer.appendChild(svg);
                }
                drawVector(svg, it);
                return;
              }

              var d = document.createElement('div');
              var s = ['position:absolute','pointer-events:none',
                'border:1.5px solid #3B6FA8','background:rgba(59,111,168,.14)'];
              if (it.kind === 'text') {
                // 文字必须**画出来**。早期实现让它落进下面的点分支：屏幕上只剩一个
                // 12px 半透明圆点，而烧录里却有字 —— 用户以为没生效又写一遍，
                // 导出图于是出现两份文字。
                // 左上角就是锚点，与 compose() 的 textBaseline:'top' 对齐。
                d.setAttribute('data-pudding-text', '');
                d.textContent = it.label;
                s = ['position:absolute','pointer-events:none',
                  'left:' + it.x + 'px','top:' + it.y + 'px',
                  'max-width:220px','padding:2px 6px','box-sizing:border-box',
                  'font:12px/1.35 "Segoe UI","Microsoft YaHei",sans-serif',
                  'color:#B23A16','background:rgba(255,255,255,.88)',
                  'border:1px solid #E4572E','border-radius:4px',
                  'white-space:pre-wrap','word-break:break-word'];
              } else if (it.kind === 'box') {
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

              // 单个删除：画错了不必清空重画。
              // 父子都是 pointer-events:none，这里必须显式恢复 auto，否则按钮点不到；
              // pointerdown 要 stopPropagation，否则会顺带开始画一个新框。
              var rm = document.createElement('button');
              rm.type = 'button';
              rm.textContent = '\u00D7';
              rm.setAttribute('data-pudding-remove', String(index));
              rm.style.cssText = 'position:absolute;top:-8px;right:-8px;width:16px;height:16px;'
                + 'border-radius:50%;border:none;background:#3B6FA8;color:#fff;'
                + 'font-size:11px;line-height:1;padding:0;cursor:pointer;pointer-events:auto';
              rm.addEventListener('pointerdown', function (ev) { ev.stopPropagation(); });
              rm.addEventListener('click', function (ev) {
                ev.stopPropagation();
                items.splice(index, 1);
                paint();
              });
              d.appendChild(rm);

              layer.appendChild(d);
            });
          }

          function local(e) {
            var b = layer.getBoundingClientRect();
            return { x: e.clientX - b.left, y: e.clientY - b.top };
          }

          function onDown(e) {
            if (!active || !box) return;
            // 右键/中键不当拖拽起点：右键会弹上下文菜单，中键会触发自动滚动。
            if (e.pointerType === 'mouse' && e.button !== 0) return;
            start = local(e);
            pushUndo();
            draft = (tool === 'pen' || tool === 'arrow') ? [start] : null;
            layer.setPointerCapture(e.pointerId);
            e.preventDefault();
          }

          // 拖拽中的实时预览：先重绘已落定的图元，再把草稿叠上去。
          function onMove(e) {
            if (!active || !draft) return;
            draft.push(local(e));
            paint();
            var svg = layer.querySelector('[data-pudding-vector]');
            if (!svg) {
              svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
              svg.setAttribute('data-pudding-vector', '');
              svg.style.cssText = 'position:absolute;left:0;top:0;width:100%;'
                + 'height:100%;overflow:visible;pointer-events:none';
              layer.appendChild(svg);
            }
            if (tool === 'pen') {
              drawVector(svg, { kind: 'scribble', points: draft });
            } else {
              var tail = draft[draft.length - 1];
              drawVector(svg, {
                kind: 'arrow', x1: draft[0].x, y1: draft[0].y, x2: tail.x, y2: tail.y
              });
            }
          }

          function onUp(e) {
            if (!active || !start) return;
            var origin = start;
            var end = local(e);
            start = null;

            if (tool === 'pen') {
              var pts = draft || [origin];
              draft = null;
              // 至少两个点才算一笔；没有落笔就把这次压的 undo 快照退掉，
              // 否则「点一下什么也没画」也会占掉一次撤销。
              if (pts.length >= 2) { items.push({ kind: 'scribble', points: pts }); }
              else { undoStack.pop(); }
              paint();
              return;
            }

            if (tool === 'arrow') {
              draft = null;
              var aw = Math.abs(end.x - origin.x), ah = Math.abs(end.y - origin.y);
              if (aw >= 4 || ah >= 4) {
                items.push({ kind: 'arrow', x1: origin.x, y1: origin.y, x2: end.x, y2: end.y });
              } else { undoStack.pop(); }
              paint();
              return;
            }

            var dx = Math.abs(end.x - origin.x), dy = Math.abs(end.y - origin.y);
            // 小于 4px 当成点选：手拖框很容易变成一个 2px 的碎框，那种框交给模型没意义。
            if (dx < 4 && dy < 4) {
              if (tool === 'text') { askText(origin.x, origin.y); return; }
              items.push({ kind: 'point', x: end.x, y: end.y });
            } else {
              items.push({ kind: 'box', x1: origin.x, y1: origin.y, x2: end.x, y2: end.y });
            }
            paint();
          }

          // 遮罩是 position:fixed 的：滚动/改窗口尺寸后图片位置变了，遮罩必须重定位。
          // 不重定位的话遮罩还停在旧位置，而 local() 按旧矩形算坐标 ——
          // 标注会静默落到错误位置，用户只会在生成图错位时才发现。
          function resync() { if (active) sync(); }

          // 快捷键只能在页面里做：XAML 的键盘加速键收不到 WebView2 内部的按键，
          // 而这里的键位只对「正在标注」有意义，本来就该用 active 收窄。
          // 页面改了工具/开关后由宿主读 state() 纠正选中态，页面不向宿主推任何消息。
          var TOOL_KEYS = { v: 'box', p: 'point', b: 'pen', a: 'arrow', t: 'text' };

          function onKey(e) {
            if (!active) return;
            var t = e.target;
            // 正在输入（文字标注框、或页面自带的搜索框）时不抢键。
            if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) {
              return;
            }
            var key = (e.key || '').toLowerCase();
            if (e.ctrlKey || e.metaKey) {
              if (key === 'z') { e.preventDefault(); undo(); }
              return;
            }
            if (key === 'escape') {
              e.preventDefault();
              window.puddingAnnotation.disable();
              return;
            }
            if (key === 'delete' || key === 'backspace') {
              if (!items.length) return;
              e.preventDefault();
              pushUndo();
              items.pop();
              paint();
              return;
            }
            if (TOOL_KEYS[key]) {
              e.preventDefault();
              window.puddingAnnotation.setTool(TOOL_KEYS[key]);
            }
          }

          function bind(l) {
            if (bound) return;
            bound = true;
            l.addEventListener('pointerdown', onDown);
            l.addEventListener('pointermove', onMove);
            l.addEventListener('pointerup', onUp);
            // 指针被系统夺走（切窗口、触控被摄）时也要落笔，否则草稿会卡在屏幕上。
            l.addEventListener('pointercancel', onUp);
            // 拖到图片外面松手也要落一个框，否则越界的拖拽会默默消失。
            document.addEventListener('pointerup', onUp);
            // capture：文字输入框自己也会 stopPropagation，靠冒泡阶段就收不到 Ctrl+Z。
            document.addEventListener('keydown', onKey, true);
            // capture=true 才能收到内部滚动容器的 scroll（scroll 不冒泡）。
            window.addEventListener('scroll', resync, true);
            window.addEventListener('resize', resync);
          }

          window.puddingAnnotation = {
            enable: function () { active = true; if (sync()) bind(layer); return !!box; },
            disable: function () { active = false; start = null; if (sync()) layer.style.display = 'none'; },
            toggle: function () {
              if (active) { this.disable(); return false; }
              return this.enable();
            },
            clear: function () { pushUndo(); items = []; if (sync()) paint(); },
            count: function () { return items.length; },
            // 工具栏切换工具：box / point / pen / arrow / text。
            setTool: function (name) {
              tool = String(name || 'box');
              draft = null;
              start = null;
              applyCursor();
              return tool;
            },
            getTool: function () { return tool; },
            // 现状快照。快捷键在页面里改工具、按 Esc 退出标注，宿主都不知道；
            // 宿主读它来纠正工具栏的选中态（页面不主动推消息）。
            state: function () {
              return {
                tool: tool,
                active: active,
                count: items.length,
                canUndo: undoStack.length > 0
              };
            },
            undo: undo,
            canUndo: function () { return undoStack.length > 0; },
            // 返回带笔迹的 PNG dataURL；形式 1（任意标记）靠它。
            compose: compose,
            // 把已存的标记画回来（制成品「继续编辑」）：传归一化 0–1000 数组。
            // 返回成功灌入的数量，0 表示图未就绪 —— 调用方应重试。
            load: loadFromPayload,
            take: function () {
              if (!sync()) return '';
              return JSON.stringify({
                src: (imageEl && imageEl.src) || '',
                // width/height 是**页面显示尺寸**，归一化坐标必须按它换算；
                // naturalWidth/naturalHeight 是图片原始像素，只用于记录「这张图到底多大」。
                // 两者不能混用：用原始像素换算归一化坐标，标注会整体偏移。
                width: box.width, height: box.height,
                naturalWidth: (imageEl && imageEl.naturalWidth) || 0,
                naturalHeight: (imageEl && imageEl.naturalHeight) || 0,
                // 原样输出：涂鸦要带 points、文字要带 label。
                // 早期按 kind 二选一的写法会把它们静默降级成 point，笔迹和原文全丢。
                items: items
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
