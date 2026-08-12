import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import TranscriptModeSwitch from './TranscriptModeSwitch';

describe('TranscriptModeSwitch', () => {
  it('renders three tiers with normal selected by default', () => {
    render(<TranscriptModeSwitch value="normal" onChange={jest.fn()} />);
    expect(screen.getByText('普通')).toBeTruthy();
    expect(screen.getByText('详细')).toBeTruthy();
    expect(screen.getByText('摘要')).toBeTruthy();
  });

  it('emits the selected tier on change', () => {
    const onChange = jest.fn();
    render(<TranscriptModeSwitch value="normal" onChange={onChange} />);
    fireEvent.click(screen.getByText('摘要'));
    expect(onChange).toHaveBeenCalledWith('summary');
    fireEvent.click(screen.getByText('详细'));
    expect(onChange).toHaveBeenCalledWith('verbose');
  });
});
