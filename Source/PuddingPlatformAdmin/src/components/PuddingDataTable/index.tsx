/**
 * PuddingDataTable — antd Table wrapper
 *
 * 封装 antd Table 默认样式和空状态，避免裸 ProTable 默认外观。
 * 不使用 ProTable 默认 options={{ density: true }}。
 */
import React from 'react';
import { Table } from 'antd';
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
}: PuddingDataTableProps<T>) {
  return (
    <div className={classNames(styles.tableSurface, className)}>
      <Table<T>
        rowKey={rowKey}
        columns={columns}
        dataSource={dataSource}
        loading={loading}
        pagination={pagination}
        onChange={onChange}
        locale={{ emptyText: emptyText ?? '暂无数据' }}
        size="middle"
      />
    </div>
  );
}

export default PuddingDataTable;
