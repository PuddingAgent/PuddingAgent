// ── ComposerTextInput：输入叶子组件（输入框性能修复，2026-08-23）──────────────
// 问题：草稿态原先住在 IntentConsole（1244 行、大量 antd 组件）里，拼音组合期间
// 每个按键 setDraftValue 都全量重渲染整个 composer；词上屏后 onInputChange 再触发
// ChatPage→ChatMain→IntentConsole 整条链。
// 修复：textarea + 草稿态 + IME 组合守卫 + 「/」命令面板全部收进本叶子，
// 按键只重渲染本叶子（memo，props 全部稳定）；IntentConsole 只在低频事件
// （词上屏 lift、hasText 空非空翻转、focus 变化、外部改写）时重渲染。
//
// 契约：
//  - 非组合输入逐键 lift（onInputChange），与旧行为一致（发送读父级 state，无竞态）；
//    组合期间不 lift，compositionEnd 一次性 lift（既有 IME 守卫语义不变）；
//  - 外部改写（引用插入/语音转写/发送清空）走 inputValue prop 同步或 ref.setValue；
//  - hasText 仅在空↔非空翻转时回调（composerActive/发送按钮门控），不逐键上报；
//  - Enter+待发图片 拦截（onEnterWithImages）与「/」面板键盘导航在叶子内消费，
//    其余按键透传父级 onKeyDown（发送快捷键等仍归 ChatPage）。
import { Input } from 'antd';
import React, {
  forwardRef,
  useCallback,
  useEffect,
  useImperativeHandle,
  useMemo,
  useRef,
  useState,
} from 'react';
import CommandPalette, {
  type Command,
  filterCommands,
} from './CommandPalette';

export interface ComposerTextInputHandle {
  /** 外部改写草稿（语音转写/uiTest 填充/发送清空）；同步 lift 到父级。 */
  setValue: (v: string) => void;
  focus: () => void;
  /** 读取当前草稿（发送组图时取提示词用；始终最新，不受 lift 时序影响）。 */
  getValue: () => string;
}

export interface ComposerTextInputProps {
  /** 父级已 lift 的输入值（外部改写源；echo 同步在叶子内抑制）。 */
  inputValue: string;
  onInputChange: (v: string) => void;
  /** 非面板/非组合按键透传（Enter 发送等归 ChatPage）。 */
  onKeyDown: (e: React.KeyboardEvent<HTMLTextAreaElement>) => void;
  onFocusChange?: (focused: boolean) => void;
  /** 空↔非空翻转时回调（仅翻转，不逐键）。 */
  onHasTextChange?: (hasText: boolean) => void;
  /** Enter（无 shift）且存在待发图片时消费按键（图片上传发送流）。 */
  onEnterWithImages?: () => void;
  hasPendingImages: boolean;
  placeholder: string;
  disabled?: boolean;
  className?: string;
  onPaste?: (e: React.ClipboardEvent<HTMLTextAreaElement>) => void;
  /** 外部持有的 textarea 引用（保持既有 textAreaRef 语义）。 */
  textareaRef: React.MutableRefObject<HTMLTextAreaElement | null>;
}

/** 「光标前是否处于 /命令 词」判定（面板显隐 + 过滤共用）。 */
const matchSlashBeforeCursor = (
  value: string,
  selectionStart: number | null,
): string | null => {
  const pos = selectionStart ?? value.length;
  const before = value.slice(0, pos);
  const match = before.match(/(?:^|\s)\/([^\s]*)$/);
  return match ? match[1] : null;
};

const ComposerTextInput = forwardRef<ComposerTextInputHandle, ComposerTextInputProps>(
  function ComposerTextInput(
    {
      inputValue,
      onInputChange,
      onKeyDown,
      onFocusChange,
      onHasTextChange,
      onEnterWithImages,
      hasPendingImages,
      placeholder,
      disabled,
      className,
      onPaste,
      textareaRef,
    },
    ref,
  ) {
    const [draftValue, setDraftValue] = useState(inputValue);
    const [paletteVisible, setPaletteVisible] = useState(false);
    const [selectedIdx, setSelectedIdx] = useState(0);
    const isTextComposingRef = useRef(false);
    const hasTextRef = useRef(inputValue.trim().length > 0);
    // 自 lift 回显抑制：父级 inputValue 与最近一次 lift 相等时是 echo，不采纳；
    // 仅当父级主动改写（引用插入/语音转写/发送清空，值 ≠ 我们 lift 过的值）时
    // 采纳进草稿。否则「父级尚未回显的滞后 prop」会被误判为外部改写，把草稿
    // 清空（lift → 采纳旧值 → 再输入 → 再采纳 的抖动回环）。
    const lastLiftedRef = useRef(inputValue);
    // getValue 需要最新草稿，但 handle 依赖保持稳定（onInputChange/textareaRef 均稳定）
    const draftValueRef = useRef(draftValue);
    draftValueRef.current = draftValue;

    useImperativeHandle(
      ref,
      () => ({
        setValue: (v: string) => {
          isTextComposingRef.current = false;
          lastLiftedRef.current = v;
          setDraftValue(v);
          onInputChange(v);
          if (!v.trim()) setPaletteVisible(false);
        },
        focus: () => textareaRef.current?.focus(),
        getValue: () => draftValueRef.current,
      }),
      [onInputChange, textareaRef],
    );

    // 外部改写同步：仅响应 inputValue prop 变化（非草稿变化），自 lift 的 echo
    // 已被 lastLiftedRef 抑制；组合中忽略（组合结束会以最终值 lift 对齐）。
    useEffect(() => {
      if (inputValue === lastLiftedRef.current) return;
      if (isTextComposingRef.current) return;
      lastLiftedRef.current = inputValue;
      setDraftValue(inputValue);
      if (!inputValue.trim()) setPaletteVisible(false);
    }, [inputValue]);

    // hasText 翻转上报（composerActive / 发送按钮门控）
    useEffect(() => {
      const next = draftValue.trim().length > 0;
      if (next !== hasTextRef.current) {
        hasTextRef.current = next;
        onHasTextChange?.(next);
      }
    }, [draftValue, onHasTextChange]);

    const updateCommandPaletteState = useCallback(
      (value: string, selectionStart?: number | null) => {
        setPaletteVisible(matchSlashBeforeCursor(value, selectionStart ?? null) !== null);
        setSelectedIdx(0);
      },
      [],
    );

    const slashFilterText = useMemo(() => {
      if (!paletteVisible) return '';
      return (
        matchSlashBeforeCursor(draftValue, textareaRef.current?.selectionStart ?? null) ??
        ''
      );
    }, [draftValue, paletteVisible, textareaRef]);

    const filteredCommands = useMemo(
      () => filterCommands(slashFilterText),
      [slashFilterText],
    );

    const handleInputChange = useCallback(
      (e: React.ChangeEvent<HTMLTextAreaElement>) => {
        const v = e.target.value;
        setDraftValue(v);
        if (!isTextComposingRef.current) {
          lastLiftedRef.current = v;
          onInputChange(v);
        }
        updateCommandPaletteState(v, e.target.selectionStart);
      },
      [onInputChange, updateCommandPaletteState],
    );

    const handleCompositionStart = useCallback(() => {
      isTextComposingRef.current = true;
    }, []);

    const handleCompositionEnd = useCallback(
      (e: React.CompositionEvent<HTMLTextAreaElement>) => {
        isTextComposingRef.current = false;
        const v = e.currentTarget.value;
        setDraftValue(v);
        lastLiftedRef.current = v;
        onInputChange(v);
        updateCommandPaletteState(v, e.currentTarget.selectionStart);
      },
      [onInputChange, updateCommandPaletteState],
    );

    const handleCommandSelect = useCallback(
      (cmd: Command) => {
        const pos = textareaRef.current?.selectionStart ?? draftValue.length;
        const before = draftValue.slice(0, pos);
        const after = draftValue.slice(pos);
        const newBefore = before.replace(/\/([^\s]*)$/, `${cmd.shortcut} `);
        const newValue = newBefore + after;
        setDraftValue(newValue);
        lastLiftedRef.current = newValue;
        onInputChange(newValue);
        setPaletteVisible(false);
        requestAnimationFrame(() => {
          if (textareaRef.current) {
            const newPos = newBefore.length;
            textareaRef.current.selectionStart = newPos;
            textareaRef.current.selectionEnd = newPos;
            textareaRef.current.focus();
          }
        });
      },
      [draftValue, onInputChange, textareaRef],
    );

    const handleKeyDown = useCallback(
      (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
        if (e.nativeEvent.isComposing || isTextComposingRef.current) {
          return;
        }

        if (paletteVisible) {
          if (e.key === 'ArrowDown') {
            e.preventDefault();
            setSelectedIdx((prev) =>
              Math.min(filteredCommands.length - 1, prev + 1),
            );
            return;
          }
          if (e.key === 'ArrowUp') {
            e.preventDefault();
            setSelectedIdx((prev) => Math.max(0, prev - 1));
            return;
          }
          if (e.key === 'Escape') {
            e.preventDefault();
            setPaletteVisible(false);
            return;
          }
          if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            const cmd = filteredCommands[selectedIdx];
            if (cmd) handleCommandSelect(cmd);
            return;
          }
        }
        if (
          e.key === 'Enter' &&
          !e.shiftKey &&
          hasPendingImages &&
          onEnterWithImages
        ) {
          e.preventDefault();
          onEnterWithImages();
          return;
        }
        onKeyDown(e);
      },
      [
        paletteVisible,
        filteredCommands,
        selectedIdx,
        handleCommandSelect,
        hasPendingImages,
        onEnterWithImages,
        onKeyDown,
      ],
    );

    const handleFocus = useCallback(() => {
      onFocusChange?.(true);
    }, [onFocusChange]);
    const handleBlur = useCallback(() => {
      onFocusChange?.(false);
    }, [onFocusChange]);

    return (
      <>
        <Input.TextArea
          ref={textareaRef as never}
          value={draftValue}
          onChange={handleInputChange}
          onKeyDown={handleKeyDown}
          onPaste={onPaste}
          onCompositionStart={handleCompositionStart}
          onCompositionEnd={handleCompositionEnd}
          onFocus={handleFocus}
          onBlur={handleBlur}
          placeholder={placeholder}
          disabled={disabled}
          autoSize={{ minRows: 1, maxRows: 5 }}
          className={className}
          data-testid="chat-input"
        />
        <CommandPalette
          visible={paletteVisible}
          filterText={slashFilterText}
          selectedIdx={selectedIdx % Math.max(1, filteredCommands.length)}
          onSelectIndex={setSelectedIdx}
          onSelect={handleCommandSelect}
          onClose={() => setPaletteVisible(false)}
        />
      </>
    );
  },
);

export default React.memo(ComposerTextInput);
