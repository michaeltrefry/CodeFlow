import { Routes } from '@angular/router';
import { authenticatedGuard } from './auth/authenticated.guard';

export const routes: Routes = [
  // HAA-6: homepage replaces the old "land on /traces" default. Traces stays first-class via
  // its own route + nav entry below.
  {
    path: '',
    pathMatch: 'full',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/home/home.component').then(m => m.HomeComponent),
  },
  {
    path: 'agents',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/agents/agents-list.component').then(m => m.AgentsListComponent)
  },
  {
    path: 'agents/new',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/agents/agent-editor.component').then(m => m.AgentEditorComponent)
  },
  {
    path: 'agents/:key/test',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/agents/agent-test.component').then(m => m.AgentTestComponent)
  },
  {
    path: 'agents/:key/edit',
    loadComponent: () => import('./pages/agents/agent-editor.component').then(m => m.AgentEditorComponent)
  },
  {
    path: 'agents/:key',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/agents/agent-detail.component').then(m => m.AgentDetailComponent)
  },
  {
    path: 'workflows',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/workflows/workflows-list.component').then(m => m.WorkflowsListComponent)
  },
  {
    path: 'workflows/new',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/workflows/editor/workflow-canvas.component').then(m => m.WorkflowCanvasComponent)
  },
  {
    path: 'workflows/:key/edit',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/workflows/editor/workflow-canvas.component').then(m => m.WorkflowCanvasComponent)
  },
  {
    path: 'workflows/:key/dry-run',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/workflows/dry-run/workflow-dry-run.component').then(m => m.WorkflowDryRunComponent)
  },
  {
    path: 'workflows/:key',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/workflows/workflow-detail.component').then(m => m.WorkflowDetailComponent)
  },
  {
    path: 'traces',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/traces/traces-list.component').then(m => m.TracesListComponent)
  },
  {
    path: 'traces/new',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/traces/trace-submit.component').then(m => m.TraceSubmitComponent)
  },
  {
    path: 'traces/:id',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/traces/trace-detail.component').then(m => m.TraceDetailComponent)
  },
  {
    path: 'hitl',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/hitl/hitl-queue.component').then(m => m.HitlQueueComponent)
  },
  {
    path: 'ops/dlq',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/ops/dlq.component').then(m => m.DlqComponent)
  },
  {
    path: 'settings/mcp-servers',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/settings/mcp-servers/mcp-servers-list.component').then(m => m.McpServersListComponent)
  },
  {
    path: 'settings/mcp-servers/new',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/settings/mcp-servers/mcp-server-editor.component').then(m => m.McpServerEditorComponent)
  },
  {
    path: 'settings/mcp-servers/:id',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/settings/mcp-servers/mcp-server-editor.component').then(m => m.McpServerEditorComponent)
  },
  {
    path: 'settings/roles',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/settings/roles/roles-list.component').then(m => m.RolesListComponent)
  },
  {
    path: 'settings/roles/new',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/settings/roles/role-editor.component').then(m => m.RoleEditorComponent)
  },
  {
    path: 'settings/roles/:id',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/settings/roles/role-editor.component').then(m => m.RoleEditorComponent)
  },
  {
    path: 'settings/skills',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/settings/skills/skills-list.component').then(m => m.SkillsListComponent)
  },
  {
    path: 'settings/skills/new',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/settings/skills/skill-editor.component').then(m => m.SkillEditorComponent)
  },
  {
    path: 'settings/skills/:id',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/settings/skills/skill-editor.component').then(m => m.SkillEditorComponent)
  },
  {
    path: 'settings/git-host',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/settings/git-host/git-host-settings.component').then(m => m.GitHostSettingsComponent)
  },
  {
    path: 'settings/llm-providers',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/settings/llm-providers/llm-providers.component').then(m => m.LlmProvidersComponent)
  },
  {
    path: 'assistant-preview',
    canActivate: [authenticatedGuard],
    loadComponent: () => import('./pages/assistant-preview/assistant-preview.component').then(m => m.AssistantPreviewComponent)
  },
  { path: '**', redirectTo: '/' }
];
