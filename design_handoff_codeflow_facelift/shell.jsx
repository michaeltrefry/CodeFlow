// shell.jsx — app shell with nav, topbar

const NAV_GROUPS = [
  { items: [
    { id: 'traces',    label: 'Traces',     icon: 'traces',    badge: '12' },
    { id: 'workflows', label: 'Workflows',  icon: 'workflows' },
    { id: 'agents',    label: 'Agents',     icon: 'agents' },
    { id: 'hitl',      label: 'HITL queue', icon: 'hitl',      badge: '3' },
    { id: 'dlq',       label: 'DLQ ops',    icon: 'dlq',       badge: '10' }
  ]},
  { label: 'Settings', items: [
    { id: 'mcp',    label: 'MCP servers', icon: 'mcp' },
    { id: 'roles',  label: 'Roles',       icon: 'roles' },
    { id: 'skills', label: 'Skills',      icon: 'skills' },
    { id: 'git',    label: 'Git host',    icon: 'git' }
  ]},
  { label: 'Design system', items: [
    { id: 'inventory', label: 'Components', icon: 'inventory' }
  ]}
];

const PAGE_TITLES = {
  traces: 'Traces', workflows: 'Workflows', agents: 'Agents', hitl: 'HITL queue',
  dlq: 'DLQ ops', mcp: 'MCP servers', roles: 'Roles', skills: 'Skills', git: 'Git host',
  inventory: 'Components', 'trace-detail': 'Trace', 'workflow-canvas': 'Workflow',
  'agent-editor': 'New agent'
};

function Shell({ active, setActive, children, subcrumb, collapsed, setCollapsed, onToggleTweaks }) {
  return (
    <div className="shell" data-nav={collapsed ? 'collapsed' : 'expanded'}>
      <aside className="nav">
        <div className="nav-brand">
          <span className="brand-mark" />
          <span className="brand-wordmark">CodeFlow</span>
        </div>
        <div className="nav-links">
          {NAV_GROUPS.map((g, gi) => (
            <React.Fragment key={gi}>
              {g.label ? <div className="nav-section-label">{g.label}</div> : <div className="nav-section-spacer" />}
              {g.items.map(it => {
                const IconC = Ico[it.icon];
                return (
                  <a key={it.id} className="nav-link" data-active={active === it.id} onClick={() => setActive(it.id)} title={it.label}>
                    <IconC />
                    <span className="nav-link-label">{it.label}</span>
                    {it.badge && <span className="nav-link-badge">{it.badge}</span>}
                  </a>
                );
              })}
            </React.Fragment>
          ))}
        </div>
        <div className="nav-footer">
          <button className="nav-toggle" onClick={() => setCollapsed(!collapsed)} title="Toggle nav">
            <Ico.panelL />
            <span className="nav-toggle-label">Collapse</span>
          </button>
          <div className="nav-user">
            <div className="nav-user-avatar">MT</div>
            <div className="nav-user-body">
              <div className="nav-user-name">Michael Trefry</div>
              <div className="nav-user-roles">
                <Chip variant="accent">admin</Chip>
                <Chip>operator</Chip>
              </div>
            </div>
          </div>
        </div>
      </aside>
      <div className="workspace">
        <div className="topbar">
          <div className="breadcrumb">
            <span>CodeFlow</span>
            <span className="sep">/</span>
            <span className="current">{PAGE_TITLES[active] || 'Home'}</span>
            {subcrumb && <><span className="sep">/</span><span className="mono" style={{color: 'var(--muted)'}}>{subcrumb}</span></>}
          </div>
          <div className="topbar-search">
            <span className="search-icon"><Ico.search /></span>
            <input placeholder="Search traces, agents, workflows…" />
            <span className="kbd">⌘K</span>
          </div>
          <button className="topbar-icon-btn" title="Notifications"><Ico.bell /><span className="dot" /></button>
          <button className="topbar-icon-btn" onClick={onToggleTweaks} title="Tweaks"><Ico.settings /></button>
        </div>
        {children}
      </div>
    </div>
  );
}

Object.assign(window, { Shell, PAGE_TITLES });
