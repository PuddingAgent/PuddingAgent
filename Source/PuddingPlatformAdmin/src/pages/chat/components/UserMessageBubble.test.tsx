import { render, screen } from '@testing-library/react';
import * as React from 'react';
import UserMessageBubble from './UserMessageBubble';

jest.mock('../styles', () => {
  const styles = new Proxy({}, { get: (_target, prop) => String(prop) });
  return {
    useChatStyles: () => ({
      styles,
      cx: (...names: Array<string | false | undefined>) =>
        names.filter(Boolean).join(' '),
    }),
  };
});

describe('UserMessageBubble voice metadata', () => {
  it('marks user messages sent from voice input', () => {
    render(
      <UserMessageBubble
        content="请总结今天的工作"
        createdAt={1000}
        status="success"
        userName="我"
        modality="voice"
        formatTime={() => '10:24'}
      />,
    );

    expect(screen.getByText('Voice')).toBeTruthy();
    expect(screen.getByText('请总结今天的工作')).toBeTruthy();
  });
});
