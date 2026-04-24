// pages-settings.jsx — MCP servers, Roles, Skills, Git host

function McpPage() {
  return (
    <div className="page">
      <PageHeader
        title="MCP servers"
        subtitle="Model Context Protocol endpoints available to all agents on this tenant."
        actions={<Button variant="primary" icon={<Ico.plus/>}>Add server</Button>}
      />
      <div className="card" style={{overflow:'hidden'}}>
        <table className="table">
          <thead><tr><th>Key</th><th>Name</th><th>Transport</th><th>Endpoint</th><th>Auth</th><th>Health</th><th>Last verified</th><th></th></tr></thead>
          <tbody>
            {CF_DATA.MCP_SERVERS.map(s => {
              const hv = s.healthStatus === 'Healthy' ? 'ok' : s.healthStatus === 'Unhealthy' ? 'err' : 'default';
              return (
                <tr key={s.key}>
                  <td className="mono" style={{fontWeight:500}}>{s.key}</td>
                  <td>{s.displayName}</td>
                  <td><Chip mono>{s.transport}</Chip></td>
                  <td className="mono small muted" style={{maxWidth:280, overflow:'hidden', textOverflow:'ellipsis', whiteSpace:'nowrap'}}>{s.endpointUrl}</td>
                  <td>{s.hasBearerToken ? <Chip mono>Bearer</Chip> : <Chip mono>none</Chip>}</td>
                  <td><Chip variant={hv} dot>{s.healthStatus}</Chip></td>
                  <td className="muted small">{s.lastVerifiedAtUtc ? relTime(s.lastVerifiedAtUtc) : '—'}</td>
                  <td className="actions"><Button size="sm">Verify</Button></td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      <Card title="Notion MCP — last verification error">
        <div className="trace-failure">
          <strong>HTTP 401 Unauthorized:</strong> Bearer token was rejected. Check the <code>NOTION_MCP_TOKEN</code> secret or re-issue from the Notion integrations page.
        </div>
      </Card>
    </div>
  );
}

function RolesPage() {
  return (
    <div className="page">
      <PageHeader
        title="Roles"
        subtitle="Assignable bundles of permissions and skill grants."
        actions={<Button variant="primary" icon={<Ico.plus/>}>New role</Button>}
      />
      <div className="card" style={{overflow:'hidden'}}>
        <table className="table">
          <thead><tr><th>Key</th><th>Display name</th><th>Grants</th><th>Skills</th><th>Description</th><th></th></tr></thead>
          <tbody>
            {CF_DATA.ROLES.map(r => (
              <tr key={r.key}>
                <td className="mono" style={{fontWeight:500}}>{r.key}</td>
                <td>{r.displayName}</td>
                <td><Chip mono>{r.grantCount} perms</Chip></td>
                <td><Chip mono>{r.skillCount} skills</Chip></td>
                <td className="muted small">{r.description}</td>
                <td className="actions"><Button size="sm">Edit</Button></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function SkillsPage() {
  const [selected, setSelected] = useState(CF_DATA.SKILLS[0].id);
  const active = CF_DATA.SKILLS.find(s => s.id === selected);
  return (
    <div className="page">
      <PageHeader
        title="Skills"
        subtitle="Reusable policy snippets. Composed into agent system prompts at build time."
        actions={<Button variant="primary" icon={<Ico.plus/>}>New skill</Button>}
      />
      <div style={{display:'grid', gridTemplateColumns:'320px 1fr', gap:16, alignItems:'flex-start'}}>
        <div className="card" style={{overflow:'hidden'}}>
          <div className="stack" style={{gap:0}}>
            {CF_DATA.SKILLS.map(s => (
              <div key={s.id} onClick={() => setSelected(s.id)}
                style={{padding:'12px 16px', borderBottom:'1px solid var(--hairline)', cursor:'default',
                  background: selected === s.id ? 'var(--surface-2)' : undefined,
                  borderLeft: selected === s.id ? '2px solid var(--accent)' : '2px solid transparent'}}>
                <div className="mono" style={{fontWeight:500}}>{s.name}</div>
                <div className="muted small" style={{marginTop:2}}>updated {relTime(s.updatedAtUtc)}</div>
              </div>
            ))}
          </div>
        </div>
        <Card title={active.name}
          right={<div className="row"><Chip mono>markdown</Chip><Button size="sm" variant="ghost">History</Button><Button size="sm" variant="primary">Save</Button></div>}>
          <div className="code-field">
            <div className="code-field-head"><span>{active.name}.md</span><span>markdown</span></div>
            <textarea className="textarea" rows={10} style={{border:0, borderRadius:0, background:'var(--bg)'}}
              defaultValue={active.body + '\n\n- Follow the rule strictly; never negotiate.\n- Log violations to the audit trail.\n- Escalate unclear cases to the compliance HITL.'}/>
          </div>
        </Card>
      </div>
    </div>
  );
}

function GitHostPage() {
  return (
    <div className="page">
      <PageHeader title="Git host" subtitle="Connect a Git provider for PR workflows and artifact storage."/>
      <Card title="Provider connection">
        <div className="form-grid">
          <Field label="Provider">
            <select className="select" defaultValue="github"><option value="github">GitHub</option><option>GitLab</option><option>Gitea</option></select>
          </Field>
          <Field label="Organization">
            <input className="input mono" defaultValue="acme-platform"/>
          </Field>
          <Field label="Default branch">
            <input className="input mono" defaultValue="main"/>
          </Field>
          <Field label="App installation">
            <div className="row">
              <Chip variant="ok" dot>Installed</Chip>
              <span className="mono small muted">installation #48291</span>
            </div>
          </Field>
          <Field label="Repositories in scope" className="span-2" hint="Workflows may only read/write repos in this list.">
            <div className="row">
              <Chip mono>acme-platform/api</Chip>
              <Chip mono>acme-platform/ui</Chip>
              <Chip mono>acme-platform/infra</Chip>
              <Button size="sm" icon={<Ico.plus/>}>Add repo</Button>
            </div>
          </Field>
        </div>
      </Card>
      <Card title="Webhook">
        <div className="stack">
          <div className="row" style={{justifyContent:'space-between'}}>
            <div>
              <div className="mono" style={{fontWeight:500}}>https://codeflow.acme.internal/hooks/git</div>
              <div className="muted small">Events: pull_request · push · issue_comment</div>
            </div>
            <Chip variant="ok" dot>Last delivery 2m ago</Chip>
          </div>
          <Field label="Shared secret">
            <div className="row" style={{width:'100%'}}>
              <input className="input mono" defaultValue="•••••••••••••••••••••••••••••••" style={{flex:1}}/>
              <Button variant="ghost">Reveal</Button>
              <Button variant="ghost" icon={<Ico.refresh/>}>Rotate</Button>
            </div>
          </Field>
        </div>
      </Card>
    </div>
  );
}

Object.assign(window, { McpPage, RolesPage, SkillsPage, GitHostPage });
