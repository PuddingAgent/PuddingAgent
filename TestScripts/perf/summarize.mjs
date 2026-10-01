// 汇总最终记录结果，输出可直接写入报告的关键指标（含 A/B 补丁场景）。
import { readFileSync } from 'node:fs';

const r = JSON.parse(readFileSync('temp/perf/out/record-results.json', 'utf8'));
const stats = (s) => (s ? `n=${s.count} p50=${s.p50} p95=${s.p95} max=${s.max}` : 'n/a');

function brief(m) {
  return [
    `paint.renderToPaint=[${stats(m.paint.renderToPaintMs)}]`,
    `paint.commitToPaint=[${stats(m.paint.commitToPaintMs)}]`,
    `markdown.commit=[${stats(m.markdown.commitMs)}]`,
    `longtask=[${stats(m.longTask)}]`,
    `layoutShift=${m.layoutShift.count}/cls=${m.layoutShift.cls}`,
    `domNodes=${m.dom.nodes}`,
  ].join('  ');
}

for (const [name, s] of Object.entries(r.scenarios)) {
  console.log(`\n======== ${name}  ok=${s.ok}  wall=${s.ms}ms ========`);
  if (!s.ok) {
    console.log('ERROR: ' + s.error);
    continue;
  }
  const v = s.value;

  // A/B 补丁场景
  if (v.baseline && v.withFlexShrink) {
    for (const key of ['baseline', 'withFlexShrink', 'withContentVisibility']) {
      const x = v[key];
      console.log(`\n [${x.label}]`);
      console.log(`   availability: mountedLong=${x.availability.mountedLong} mountedScrollHeight=${x.availability.mountedScrollHeight} afterTopScrollHeight=${x.availability.afterTopScrollHeight} recoveredLong=${x.availability.recoveredLong} maxScrollTop=${x.availability.maxScrollTop} contentComputedHeight=${x.availability.contentComputedHeight} flexShrink=${x.availability.contentFlexShrink}`);
      console.log(`   ${brief(x.metrics)}`);
    }
    continue;
  }

  const m = v.metrics;
  console.log(brief(m));
  console.log(
    ` rows=${v.summary.rowCount} virtualized=${v.summary.virtualized} listScrollHeight=${v.summary.listScrollHeight} contentComputedHeight=${v.summary.contentComputedHeight} contentFlexShrink=${v.summary.contentFlexShrink}`,
  );
  console.log(` bodyTextLen=${v.summary.bodyTextLen} longRowFound=${v.summary.longRowFound} longRowHeight=${v.summary.longRowHeight} longRowTextLen=${v.summary.longRowTextLen} longRowChildNodes=${v.summary.longRowChildNodes}`);
  if (m.typewriter.inputs || m.typewriter.ticks) console.log(` typewriter=${JSON.stringify(m.typewriter)}`);
  if (v.scrolled) console.log(` scrolled=${JSON.stringify(v.scrolled)}`);
  if (v.prepend) console.log(` prepend=${JSON.stringify(v.prepend)}`);
  if (v.typing) console.log(` typing=${JSON.stringify(v.typing)}`);
  if (v.stubStats) {
    const st = v.stubStats;
    console.log(` stub: conversation=${st.conversationFulfilled} events=${st.eventsFulfilled} lens=${JSON.stringify((st.conversationContentLens || []).slice(0, 20))}`);
  }
  const cacheSteps = (m.workflow || []).filter((w) => /cache|select\./.test(w.step || ''));
  for (const w of cacheSteps.slice(0, 10)) {
    console.log(`   ${w.workflow}.${w.step} at=${w.at} ${w.durationMs}ms cacheHit=${w.cacheHit} msgs=${w.messageCount}`);
  }
  const polls = (m.workflow || []).filter((w) => w.workflow === 'agent.selectedSync');
  if (polls.length) {
    const d = polls.map((p) => p.durationMs).filter((x) => typeof x === 'number');
    console.log(` agent.selectedSync polls=${polls.length} durations=${JSON.stringify(d.slice(0, 24))}`);
  }
}
