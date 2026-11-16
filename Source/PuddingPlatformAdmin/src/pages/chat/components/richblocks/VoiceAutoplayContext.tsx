import React, {
  createContext,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';

interface VoiceAutoplayPolicy {
  allowAutoPlay: boolean;
  claim: (text: string) => boolean;
  onManualControl: () => void;
}

export const VoiceAutoplayContext = createContext<VoiceAutoplayPolicy | null>(
  null,
);

/** 消息级围栏：只朗读本页观察到生成并成功完成的回复，跨 Markdown 重挂载去重。 */
export const VoiceAutoplayScope: React.FC<{
  generating: boolean;
  completed?: boolean;
  children: React.ReactNode;
}> = ({ generating, completed = false, children }) => {
  const [observedGeneration, setObservedGeneration] = useState(false);
  const playedTexts = useRef(new Set<string>());
  const manuallyControlled = useRef(false);

  useEffect(() => {
    if (generating) setObservedGeneration(true);
  }, [generating]);

  const claim = useCallback((text: string) => {
    if (manuallyControlled.current || playedTexts.current.has(text))
      return false;
    playedTexts.current.add(text);
    return true;
  }, []);
  const onManualControl = useCallback(() => {
    // 手动停止/播放过的回复，收尾或重挂载后也不再擅自启动。
    manuallyControlled.current = true;
  }, []);
  const policy = useMemo(
    () => ({
      allowAutoPlay: observedGeneration && !generating && completed,
      claim,
      onManualControl,
    }),
    [observedGeneration, generating, completed, claim, onManualControl],
  );

  return (
    <VoiceAutoplayContext.Provider value={policy}>
      {children}
    </VoiceAutoplayContext.Provider>
  );
};
