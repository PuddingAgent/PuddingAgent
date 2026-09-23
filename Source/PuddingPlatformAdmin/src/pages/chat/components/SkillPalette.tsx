// ── SkillPalette：`+` 菜单 →「技能」面板（搜索 + 选择）────────
// 交互定调（用户 2026-09-23）：选中技能后**转换为文本附加到本轮会话**，
// 提示 Agent 可使用该技能。因此本组件不依赖任何后端「本轮技能」参数 ——
// SubmitConversationTurnRequest 只有 clientRequestId/clientMessageId/
// recipients/content/metadata，技能仅绑在 Agent 配置（skillPackageIds）上。
// 数据源：listSkillPackages(enabledOnly) —— 全局技能包，无需 workspaceId。
import {
  listSkillPackages,
  type SkillPackageDto,
} from '@/services/platform/api';
import React, { useEffect, useMemo, useState } from 'react';

/** 按 name / description 子串过滤（大小写不敏感）。 */
export function filterSkillPackages(
  list: SkillPackageDto[],
  filterText: string,
): SkillPackageDto[] {
  const q = filterText.trim().toLowerCase();
  if (!q) return list;
  return list.filter(
    (s) =>
      s.name.toLowerCase().includes(q) ||
      (s.description ?? '').toLowerCase().includes(q),
  );
}

interface SkillPaletteProps {
  open: boolean;
  onClose: () => void;
  onSelect: (skill: SkillPackageDto) => void;
}

const wrapStyle: React.CSSProperties = {
  width: 280,
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
  maxHeight: 220,
  overflowY: 'auto',
};

const noteStyle: React.CSSProperties = {
  fontSize: 11,
  lineHeight: 1.5,
  padding: '2px 0',
  color: 'color-mix(in srgb, var(--text-primary) 50%, transparent)',
};

function itemStyle(): React.CSSProperties {
  return {
    padding: '7px 8px',
    borderRadius: 6,
    cursor: 'pointer',
    display: 'flex',
    flexDirection: 'column',
    gap: 2,
  };
}

const SkillPalette: React.FC<SkillPaletteProps> = ({
  open,
  onClose,
  onSelect,
}) => {
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [skills, setSkills] = useState<SkillPackageDto[]>([]);
  const [filterText, setFilterText] = useState('');

  useEffect(() => {
    if (!open) return undefined;
    let cancelled = false;
    setLoading(true);
    setError(null);
    listSkillPackages(true)
      .then((list) => {
        if (!cancelled) setSkills(list ?? []);
      })
      .catch((e) => {
        if (!cancelled) {
          setError(e instanceof Error ? e.message : '技能列表加载失败');
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
    () => filterSkillPackages(skills, filterText),
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
            {skills.length === 0 ? '暂无已启用的技能包' : '没有匹配的技能'}
          </div>
        )}
        {!loading &&
          !error &&
          filtered.map((s) => (
            <div
              key={s.skillPackageId}
              style={itemStyle()}
              role="button"
              tabIndex={0}
              data-skill-item
              onClick={() => onSelect(s)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') onSelect(s);
              }}
            >
              <span style={{ fontSize: 13, color: 'var(--text-primary)' }}>
                {s.name}
              </span>
              {s.description && (
                <span
                  style={{
                    fontSize: 11,
                    color:
                      'color-mix(in srgb, var(--text-primary) 52%, transparent)',
                  }}
                >
                  {s.description}
                </span>
              )}
            </div>
          ))}
      </div>
      {/* 诚实边界：本轮技能无法作为协议参数下发，只能以文本提示方式附加。 */}
      <div style={noteStyle}>
        选择后会在输入框末尾附加一行提示文本，由 Agent 据此使用该技能。
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
