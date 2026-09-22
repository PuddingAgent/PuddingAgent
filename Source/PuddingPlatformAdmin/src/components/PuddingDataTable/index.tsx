/**
 * PuddingDataTable — antd Table wrapper
 *
 * 封装 antd Table 默认样式和空状态，避免裸 ProTable 默认外观。
 * 不使用 ProTable 默认 options={{ density: true }}。
 */
import React from 'react';
import { Table } from 'antd';
import type { TableProps } from 'antd';
import type { ColumnsType, TablePaginationConfig } from 'antd/es/table';
import classNames from 'classnames';
import styles from './styles';

export interface PuddingDataTableProps<T extends object> {
  rowKey: string | ((record: T) => string);
  columns: ColumnsType<T>;
  dataSource: T[];
  loading?: boolean;
  emptyText?: React.ReactNode;
  pagination?: TablePaginationConfig | false;
  className?: string;
  onChange?: (pagination: TablePaginationConfig, filters: any, sorter: any) => void;
  /** 行勾选（列表批量操作）。 */
  rowSelection?: TableProps<T>['rowSelection'];
  /** 透传 antd Table 的 scroll（横向/纵向滚动区），默认 undefined ⇒ 行为与改动前一致。 */
  scroll?: TableProps<T>['scroll'];
  /** 透传 antd Table 的 sticky（表头吸顶）。不传时 antd 默认行为不变。 */
  sticky?: TableProps<T>['sticky'];
  /** 最外层容器的内联样式，与 className 并存。 */
  style?: React.CSSProperties;
}

export function PuddingDataTable<T extends object>({
  rowKey,
  columns,
  dataSource,
  loading,
  emptyText,
  pagination,
  className,
  onChange,
  rowSelection,
  scroll,
  sticky,
  style,
}: PuddingDataTableProps<T>) {
  return (
    <div className={classNames(styles.tableSurface, className)} style={style}>
      <Table<T>
        rowKey={rowKey}
        columns={columns}
        dataSource={dataSource}
        loading={loading}
        pagination={pagination}
        onChange={onChange}
        rowSelection={rowSelection}
        scroll={scroll}
        sticky={sticky}
        locale={{ emptyText: emptyText ?? '暂无数据' }}
        size="middle"
      />
    </div>
  );
}

export default PuddingDataTable;
