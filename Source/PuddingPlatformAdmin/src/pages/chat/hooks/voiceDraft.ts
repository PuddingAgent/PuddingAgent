/** Owns only the inserted voice range. Outside edits survive replacement and cancellation. */
export function createVoiceDraft(original: string, caret: number) {
  let value = original;
  let start = Math.max(0, Math.min(caret, original.length));
  let end = start;
  let conflict = false;
  const observe = (current: string) => {
    if (current === value) return conflict;
    let left = 0;
    while (left < value.length && left < current.length && value[left] === current[left]) left++;
    let oldRight = value.length; let newRight = current.length;
    while (oldRight > left && newRight > left && value[oldRight - 1] === current[newRight - 1]) { oldRight--; newRight--; }
    const delta = current.length - value.length;
    if (oldRight <= start) { start += delta; end += delta; }
    else if (left < end) conflict = true;
    value = current;
    return conflict;
  };
  const replace = (current: string, text: string) => {
    if (observe(current)) return current;
    value = current.slice(0, start) + text + current.slice(end);
    end = start + text.length;
    return value;
  };
  return { observe, replace, rollback: (current: string) => replace(current, '') };
}
