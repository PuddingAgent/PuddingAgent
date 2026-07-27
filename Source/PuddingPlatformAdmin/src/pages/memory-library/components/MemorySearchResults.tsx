import { List, Typography, Tag, Empty } from 'antd';
import { SearchOutlined } from '@ant-design/icons';
import React from 'react';
import type { MemorySearchResultDto } from '../types';

const { Text, Paragraph } = Typography;

interface MemorySearchResultsProps {
  results: MemorySearchResultDto[];
  /** 点击搜索结果时回调，传入 bookId 和 chapterId。 */
  onSelect: (bookId: string, chapterId: string) => void;
}

const MemorySearchResults: React.FC<MemorySearchResultsProps> = ({
  results,
  onSelect,
}) => {
  if (!results.length) {
    return <Empty description="无搜索结果" />;
  }

  return (
    <List
      className="memory-search-results"
      size="small"
      dataSource={results}
      renderItem={(item) => (
        <List.Item
          className="memory-search-result"
          role="button"
          tabIndex={0}
          onClick={() => onSelect(item.bookId, item.chapterId)}
          onKeyDown={(event) => {
            if (event.key === 'Enter' || event.key === ' ') {
              event.preventDefault();
              onSelect(item.bookId, item.chapterId);
            }
          }}
        >
          <List.Item.Meta
            avatar={<SearchOutlined style={{ color: '#1890ff' }} />}
            title={
              <Text strong className="memory-search-result-title">{item.bookTitle}</Text>
            }
            description={
              <div>
                <Paragraph
                  ellipsis={{ rows: 2 }}
                  className="memory-search-result-snippet"
                >
                  {item.snippet}
                </Paragraph>
                <Tag bordered={false} className="memory-search-result-score">
                  相关度: {item.score.toFixed(2)}
                </Tag>
              </div>
            }
          />
        </List.Item>
      )}
    />
  );
};

export default MemorySearchResults;
