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

  it('renders every image attached to one user message', () => {
    render(
      <UserMessageBubble
        content="比较图片"
        createdAt={1000}
        status="success"
        userName="我"
        modality="image"
        visionArtifactIds={['vision-a', 'vision-b']}
        workspaceId="default"
        formatTime={() => '10:24'}
      />,
    );

    expect(screen.getByAltText('比较图片 1/2')).toBeTruthy();
    expect(screen.getByAltText('比较图片 2/2')).toBeTruthy();
  });
});
