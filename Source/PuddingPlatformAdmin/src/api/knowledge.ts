import request from '@/utils/request'

export interface KnowledgeDocument {
  documentId: string
  workspaceId: string
  content: string
  title?: string
  sourcePath?: string
  indexedAt: string
  metadata?: Record<string, string>
}

export interface KnowledgeSearchResult {
  documentId: string
  content: string
  title?: string
  score: number
}

export interface GraphEntity {
  entityId: string
  workspaceId: string
  type: string
  label: string
  properties: Record<string, string>
}

export interface GraphRelation {
  relationId: string
  workspaceId: string
  fromEntityId: string
  toEntityId: string
  relationType: string
  weight: number
}

export interface GraphQueryRequest {
  keyword?: string
  entityType?: string
  limit?: number
}

export interface StorageObjectMeta {
  objectId: string
  workspaceId: string
  path: string
  sizeBytes: number
  contentType?: string
  createdAt: string
}

export function getKnowledgeDocuments(workspaceId: string) {
  return request({
    url: `/api/knowledge/${encodeURIComponent(workspaceId)}/documents`,
    method: 'get',
  }) as Promise<KnowledgeDocument[]>
}

export function searchKnowledge(workspaceId: string, query: string, topK = 5) {
  return request({
    url: `/api/knowledge/${encodeURIComponent(workspaceId)}/search`,
    method: 'post',
    data: { query, topK },
  }) as Promise<KnowledgeSearchResult[]>
}

export function queryGraphEntities(workspaceId: string, req: GraphQueryRequest) {
  return request({
    url: `/api/graph/${encodeURIComponent(workspaceId)}/entities/query`,
    method: 'post',
    data: req,
  }) as Promise<GraphEntity[]>
}

export function getGraphStats(workspaceId: string) {
  return request({
    url: `/api/graph/${encodeURIComponent(workspaceId)}/stats`,
    method: 'get',
  }) as Promise<{ workspaceId: string; entities: number; relations: number }>
}

export function listStorageObjects(workspaceId: string, prefix?: string) {
  return request({
    url: `/api/storage/${encodeURIComponent(workspaceId)}/objects`,
    method: 'get',
    params: { prefix },
  }) as Promise<StorageObjectMeta[]>
}
