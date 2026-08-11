import React from 'react';

type ChatStylesValue = ReturnType<typeof import('../styles')['useChatStyles']>;

const ChatMessageStyleContext = React.createContext<ChatStylesValue | null>(
  null,
);

interface ChatMessageStyleProviderProps {
  value: ChatStylesValue;
  children: React.ReactNode;
}

/**
 * Shares the single MessageList style registry with every visible message row.
 * Message leaves must not call the aggregate useChatStyles hook themselves:
 * doing so subscribes each row to every chat style domain.
 */
export const ChatMessageStyleProvider: React.FC<
  ChatMessageStyleProviderProps
> = ({ value, children }) => (
  <ChatMessageStyleContext.Provider value={value}>
    {children}
  </ChatMessageStyleContext.Provider>
);

const testStyles = new Proxy<Record<string, string>>(
  {},
  {
    get: (_target, property) => String(property),
  },
);

const testValue = {
  styles: testStyles,
  cx: (...values: Array<string | false | null | undefined>) =>
    values.filter(Boolean).join(' '),
  theme: undefined,
} as unknown as ChatStylesValue;

export const useChatMessageStyles = (): ChatStylesValue => {
  const value = React.useContext(ChatMessageStyleContext);
  if (value) return value;

  // Leaf component tests intentionally render without the full MessageList.
  // Production code must keep the provider boundary explicit so a regression
  // cannot silently recreate the per-row aggregate style subscriptions.
  if (process.env.NODE_ENV === 'test') return testValue;
  throw new Error(
    'Message components must render inside ChatMessageStyleProvider.',
  );
};
