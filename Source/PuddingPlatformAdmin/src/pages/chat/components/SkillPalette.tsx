// ── SkillPalette：`+` 菜单 →「技能」面板（搜索 + 选择）────────
// 交互定调（用户 2026-09-23）：选中技能后挂为 chip，**发送时**才转为文本附加到本轮。
//
// 数据源：SKILL Hub —— listHubSkills() → GET /api/skill-hub/skills
// ⚠️ 曾误用 listSkillPackages()（/api/skill-packages）：那是「上传的技能包」，本机
//    SkillPackages 表 0 行、WorkspaceSkills 1 行，而真正的技能库是 HubSkills（7 行）。
//    误用导致面板显示「暂无已启用的技能包」——不是没数据，是查错了表。
import {
  listHubSkills,
  type HubSkillSummaryDto,
} from '@/services/platform/api';
import React, { useEffect, useMemo, useState } from 'react';

/** 按 skillId / name / summary 子串过滤（大小写不敏感）。 */
export function filterHubSkills(
  list: HubSkillSummaryDto[],
  filterText: string,
): HubSkillSummaryDto[] {
  const q = filterText.trim().toLowerCase();
  if (!q) return list;
  return list.filter(
    (s) =>
      s.skillId.toLowerCase().includes(q) ||
      s.name.toLowerCase().includes(q) ||
      (s.summary ?? '').toLowerCase().includes(q),
  );
}

interface SkillPaletteProps {
  open: boolean;
  onClose: () => void;
  onSelect: (skill: HubSkillSummaryDto) => void;
}

const wrapStyle: React.CSSProperties = {
  width: 300,
  display: 'flex',
  flexDirection: 'column',
  gap: 6,
};

const titleStyle: React.CSSProperties = {
  fontSize: 12,
  fontWeight: 500,
  color: 'color-mix(in srgb, var(--text-primary) 70%, transparent)',
};

const searchStyle: React.CSSProperties = {
  width: '100%',
  boxSizing: 'border-box',
  padding: '6px 8px',
  fontSize: 13,
  borderRadius: 6,
  border: '1px solid color-mix(in srgb, var(--earth-brown) 18%, transparent)',
  background: 'transparent',
  color: 'var(--text-primary)',
  outline: 'none',
};

const listStyle: React.CSSProperties = {
  maxHeight: 240,
  overflowY: 'auto',
};

const noteStyle: React.CSSProperties = {
  fontSize: 11,
  lineHeight: 1.5,
  padding: '2px 0',
  color: 'color-mix(in srgb, var(--text-primary) 50%, transparent)',
};

const itemStyle: React.CSSProperties = {
  padding: '7px 8px',
  borderRadius: 6,
  cursor: 'pointer',
  display: 'flex',
  flexDirection: 'column',
  gap: 2,
};

const itemSubStyle: React.CSSProperties = {
  fontSize: 11,
  lineHeight: 1.4,
  color: 'color-mix(in srgb, var(--text-primary) 52%, transparent)',
  display: '-webkit-box',
  WebkitLineClamp: 2,
  WebkitBoxOrient: 'vertical',
  overflow: 'hidden',
};

const SkillPalette: React.FC<SkillPaletteProps> = ({
  open,
  onClose,
  onSelect,
}) => {
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [skills, setSkills] = useState<HubSkillSummaryDto[]>([]);
  const [filterText, setFilterText] = useState('');

  useEffect(() => {
    if (!open) return undefined;
    let cancelled = false;
    setLoading(true);
    setError(null);
    listHubSkills({ status: 'active', pageSize: 100 })
      .then((list) => {
        if (!cancelled) setSkills(Array.isArray(list) ? list : []);
      })
      .catch((e) => {
        if (!cancelled) {
          setError(e instanceof Error ? e.message : '技能库加载失败');
        }
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [open]);

  const filtered = useMemo(
    () => filterHubSkills(skills, filterText),
    [skills, filterText],
  );

  if (!open) return null;

  return (
    <div style={wrapStyle} data-testid="skill-palette">
      <div style={titleStyle}>选择技能（附加到本轮）</div>
      <input
        style={searchStyle}
        value={filterText}
        onChange={(e) => setFilterText(e.target.value)}
        placeholder="搜索技能"
        aria-label="搜索技能"
        data-testid="skill-palette-search"
      />
      <div style={listStyle}>
        {loading && <div style={noteStyle}>加载中…</div>}
        {!loading && error && <div style={noteStyle}>{error}</div>}
        {!loading && !error && filtered.length === 0 && (
          <div style={noteStyle}>
            {skills.length === 0 ? '技能库暂无技能' : '没有匹配的技能'}
          </div>
        )}
        {!loading &&
          !error &&
          filtered.map((s) => (
            <div
              key={s.skillId}
              style={itemStyle}
              role="button"
              tabIndex={0}
              title={s.skillId}
              data-skill-item
              data-testid={`skill-item-${s.skillId}`}
              onClick={() => onSelect(s)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') onSelect(s);
              }}
            >
              <span style={{ fontSize: 13, color: 'var(--text-primary)' }}>
                {s.name}
              </span>
              {(s.summary || s.description) && (
                <span style={itemSubStyle}>{s.summary || s.description}</span>
              )}
            </div>
          ))}
      </div>
      {/* 诚实边界：本轮技能不能作为协议参数下发，只能以文本提示方式附加。 */}
      <div style={noteStyle}>
        选择后发送时会在消息末尾附加一行提示，由 Agent 据此使用该技能。
      </div>
      <button
        type="button"
        style={{ ...searchStyle, cursor: 'pointer', textAlign: 'center' }}
        onClick={onClose}
        aria-label="返回"
      >
        返回
      </button>
    </div>
  );
};

export default SkillPalette;
