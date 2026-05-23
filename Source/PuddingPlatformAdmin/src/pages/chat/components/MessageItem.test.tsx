import { render, screen } from '@testing-library/react';
import * as React from 'react';
import MessageItem from './MessageItem';

jest.mock('../styles', () => {
  const styles = new Proxy({}, {
    get: (_target, prop) => String(prop),
  });
  return {
    useChatStyles: () => ({
      styles,
    }),
  };
});

jest.mock('prismjs', () => ({
  highlightElement: jest.fn(),
}));

describe('MessageItem markdown code rendering', () => {
  it('keeps inline code inline instead of nesting a block pre inside a paragraph', () => {
    const { container } = render(<MessageItem markdownText="查看 `/etc/os-release` 文件" />);

    expect(container.querySelector('pre')).toBeNull();
    expect(screen.getByText('/etc/os-release').tagName.toLowerCase()).toBe('code');
  });
});
