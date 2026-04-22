import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: '/traces' },
  {
    path: 'agents',
    loadComponent: () => import('./pages/agents/agents-list.component').then(m => m.AgentsListComponent)
  },
  {
    path: 'agents/new',
    loadComponent: () => import('./pages/agents/agent-editor.component').then(m => m.AgentEditorComponent)
  },
  {
    path: 'agents/:key/test',
    loadComponent: () => import('./pages/agents/agent-test.component').then(m => m.AgentTestComponent)
  },
  {
    path: 'agents/:key/edit',
    loadComponent: () => import('./pages/agents/agent-editor.component').then(m => m.AgentEditorComponent)
  },
  {
    path: 'agents/:key',
    loadComponent: () => import('./pages/agents/agent-detail.component').then(m => m.AgentDetailComponent)
  },
  {
    path: 'workflows',
    loadComponent: () => import('./pages/workflows/workflows-list.component').then(m => m.WorkflowsListComponent)
  },
  {
    path: 'workflows/new',
    loadComponent: () => import('./pages/workflows/editor/workflow-canvas.component').then(m => m.WorkflowCanvasComponent)
  },
  {
    path: 'workflows/:key/edit',
    loadComponent: () => import('./pages/workflows/editor/workflow-canvas.component').then(m => m.WorkflowCanvasComponent)
  },
  {
    path: 'workflows/:key',
    loadComponent: () => import('./pages/workflows/workflow-detail.component').then(m => m.WorkflowDetailComponent)
  },
  {
    path: 'traces',
    loadComponent: () => import('./pages/traces/traces-list.component').then(m => m.TracesListComponent)
  },
  {
    path: 'traces/new',
    loadComponent: () => import('./pages/traces/trace-submit.component').then(m => m.TraceSubmitComponent)
  },
  {
    path: 'traces/:id',
    loadComponent: () => import('./pages/traces/trace-detail.component').then(m => m.TraceDetailComponent)
  },
  {
    path: 'hitl',
    loadComponent: () => import('./pages/hitl/hitl-queue.component').then(m => m.HitlQueueComponent)
  },
  {
    path: 'ops/dlq',
    loadComponent: () => import('./pages/ops/dlq.component').then(m => m.DlqComponent)
  },
  {
    path: 'settings/mcp-servers',
    loadComponent: () => import('./pages/settings/mcp-servers/mcp-servers-list.component').then(m => m.McpServersListComponent)
  },
  {
    path: 'settings/mcp-servers/new',
    loadComponent: () => import('./pages/settings/mcp-servers/mcp-server-editor.component').then(m => m.McpServerEditorComponent)
  },
  {
    path: 'settings/mcp-servers/:id',
    loadComponent: () => import('./pages/settings/mcp-servers/mcp-server-editor.component').then(m => m.McpServerEditorComponent)
  },
  {
    path: 'settings/roles',
    loadComponent: () => import('./pages/settings/roles/roles-list.component').then(m => m.RolesListComponent)
  },
  {
    path: 'settings/roles/new',
    loadComponent: () => import('./pages/settings/roles/role-editor.component').then(m => m.RoleEditorComponent)
  },
  {
    path: 'settings/roles/:id',
    loadComponent: () => import('./pages/settings/roles/role-editor.component').then(m => m.RoleEditorComponent)
  },
  {
    path: 'settings/skills',
    loadComponent: () => import('./pages/settings/skills/skills-list.component').then(m => m.SkillsListComponent)
  },
  {
    path: 'settings/skills/new',
    loadComponent: () => import('./pages/settings/skills/skill-editor.component').then(m => m.SkillEditorComponent)
  },
  {
    path: 'settings/skills/:id',
    loadComponent: () => import('./pages/settings/skills/skill-editor.component').then(m => m.SkillEditorComponent)
  },
  {
    path: 'settings/git-host',
    loadComponent: () => import('./pages/settings/git-host/git-host-settings.component').then(m => m.GitHostSettingsComponent)
  },
  { path: '**', redirectTo: '/traces' }
];
