// pages-agents.jsx — Agents grid + Agent editor

function AgentsPage({ onNewAgent, onEditAgent }) {
  const [filter, setFilter] = useState('all');
  const agents = CF_DATA.AGENTS.filter(a => filter === 'all' ? true : filter === a.type);

  return (
    <div className="page">
      <PageHeader
        title="Agents"
        subtitle="Named, versioned prompt + model bundles. Each version is immutable; latest is rotated in on new runs unless pinned."
        actions={<>
          <Button variant="ghost" icon={<Ico.refresh/>}>Refresh</Button>
          <Button variant="primary" icon={<Ico.plus/>} onClick={onNewAgent}>New agent</Button>
        </>}
      />

      <div className="list-toolbar">
        <div className="list-toolbar-left">
          <Segmented value={filter} onChange={setFilter} options={[
            { value: 'all', label: `All (${CF_DATA.AGENTS.length})` },
            { value: 'agent', label: `LLM (${CF_DATA.AGENTS.filter(a => a.type === 'agent').length})` },
            { value: 'hitl', label: `HITL (${CF_DATA.AGENTS.filter(a => a.type === 'hitl').length})` }
          ]} />
        </div>
        <span className="muted small">showing {agents.length}</span>
      </div>

      <div className="agent-grid">
        {agents.map(a => (
          <div key={a.key} className="agent-card" onClick={() => onEditAgent(a)}>
            <div className="agent-card-head">
              <div style={{minWidth:0, flex:1}}>
                <div className="agent-key">{a.key}</div>
                <div className="agent-name">{a.name}</div>
              </div>
              <div className={`agent-type-ico ${a.type === 'hitl' ? 'hitl' : ''}`}>
                {a.type === 'hitl' ? <Ico.hitl /> : <Ico.bot />}
              </div>
            </div>
            <div className="agent-tags">
              <Chip variant="accent" mono>v{a.latestVersion}</Chip>
              {a.type === 'hitl'
                ? <Chip mono>hitl</Chip>
                : <>
                    <Chip mono><ProviderIco provider={a.provider}/> {a.provider}</Chip>
                    <Chip mono>{a.model}</Chip>
                  </>
              }
            </div>
            <div className="agent-stamp">
              <span>updated {relTime(a.latestCreatedAtUtc)}</span>
              <span>·</span>
              <span className="mono">@{a.latestCreatedBy}</span>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

function AgentEditorPage({ agent, onBack }) {
  const [tab, setTab] = useState('prompt');
  const isNew = !agent;
  const a = agent || { key: '', name: '', provider: 'anthropic', model: 'claude-sonnet-4-5', type: 'agent', latestVersion: 1 };

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <Button size="sm" variant="ghost" icon={<Ico.back/>} onClick={onBack}>Agents</Button>
          <h1 style={{marginTop:8}}>{isNew ? 'New agent' : a.name}</h1>
          <div className="trace-header-meta">
            {!isNew && <>
              <Chip mono>{a.key}</Chip>
              <Chip mono variant="accent">v{a.latestVersion} → v{a.latestVersion + 1} draft</Chip>
              <Chip>{a.type}</Chip>
            </>}
            {isNew && <Chip variant="accent">draft</Chip>}
          </div>
        </div>
        <div className="page-header-actions">
          <Button variant="ghost">Discard</Button>
          <Button>Save draft</Button>
          <Button variant="primary" icon={<Ico.check/>}>{isNew ? 'Create v1' : `Publish v${a.latestVersion + 1}`}</Button>
        </div>
      </div>

      <div className="card" style={{padding:'0 20px'}}>
        <Tabs value={tab} onChange={setTab} items={[
          { value: 'identity', label: 'Identity' },
          { value: 'prompt',   label: 'Prompt & output' },
          { value: 'model',    label: 'Model' },
          { value: 'skills',   label: 'Skills', count: 3 },
          { value: 'outputs',  label: 'Output ports', count: 3 },
          { value: 'history',  label: 'Versions' }
        ]} />
      </div>

      {tab === 'identity' && (
        <div className="card">
          <div className="form-section">
            <div className="form-section-head">
              <h3>Identity</h3>
              <p>Key is immutable once created. Rename the display name freely.</p>
            </div>
            <div className="form-grid">
              <Field label="Agent key" hint={<>Lowercase, hyphenated. Used in workflow nodes as <code>agentKey</code>.</>}>
                <input className="input mono" defaultValue={a.key || 'new-agent'} disabled={!isNew} />
              </Field>
              <Field label="Display name">
                <input className="input" defaultValue={a.name || 'New agent'} />
              </Field>
              <Field label="Type">
                <div className="seg" style={{width:'fit-content'}}>
                  <button data-active={a.type === 'agent'}>LLM agent</button>
                  <button data-active={a.type === 'hitl'}>HITL</button>
                </div>
              </Field>
              <Field label="Owner team">
                <select className="select" defaultValue="platform"><option value="platform">Platform</option><option>Support</option><option>Dev-tools</option></select>
              </Field>
              <Field label="Description" className="span-2">
                <textarea className="textarea" rows={2} style={{fontFamily:'var(--font-sans)',fontSize:'var(--fs-md)'}}
                  defaultValue="Classifies inbound support tickets by category and priority for downstream routing."/>
              </Field>
            </div>
          </div>
        </div>
      )}

      {tab === 'prompt' && (
        <div className="card">
          <div className="form-section">
            <div className="form-section-head">
              <h3>System prompt</h3>
              <p>Prepended to every call. Keep instruction-focused and stable.</p>
            </div>
            <div className="code-field">
              <div className="code-field-head"><span>system.md</span><span>markdown · 284 tokens</span></div>
              <textarea className="textarea" rows={8} style={{border:0, borderRadius:0, background:'var(--bg)'}}
                defaultValue={`You are a senior support-triage agent for CodeFlow.
Classify each ticket into one of: billing, technical, onboarding, security.
Emit priority p1|p2|p3 based on the ticket body and any attached SLA metadata.

If a customer quotes an MSA section or requests legal review, mark priority=p1
and route to the compliance HITL via the "p1" output port.`}/>
            </div>
          </div>
          <div className="form-section">
            <div className="form-section-head">
              <h3>Input template</h3>
              <p>Rendered per-round with context variables. Mustache-style.</p>
            </div>
            <div className="code-field">
              <div className="code-field-head"><span>input.hbs</span><span>handlebars</span></div>
              <textarea className="textarea" rows={5} style={{border:0, borderRadius:0, background:'var(--bg)'}}
                defaultValue={`Ticket:
  subject: {{input.subject}}
  body: {{input.body}}
  region: {{ctx.tenant.region}}
  sla: {{ctx.tenant.slaTier}}`}/>
            </div>
          </div>
          <div className="form-section">
            <div className="form-section-head">
              <h3>Output schema</h3>
              <p>JSON schema for the agent's decision payload. Used for port routing.</p>
            </div>
            <div className="code-field">
              <div className="code-field-head"><span>output.schema.json</span><span>json · valid</span></div>
              <textarea className="textarea mono" rows={8} style={{border:0, borderRadius:0, background:'var(--bg)'}}
                defaultValue={`{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "required": ["category", "priority", "reasoning"],
  "properties": {
    "category": { "enum": ["billing","technical","onboarding","security"] },
    "priority": { "enum": ["p1","p2","p3"] },
    "reasoning": { "type": "string", "maxLength": 400 }
  }
}`}/>
            </div>
          </div>
        </div>
      )}

      {tab === 'model' && (
        <div className="card">
          <div className="form-section">
            <div className="form-section-head">
              <h3>Model</h3>
              <p>Provider, model id and generation parameters.</p>
            </div>
            <div className="form-grid">
              <Field label="Provider">
                <select className="select" defaultValue={a.provider}>
                  <option value="anthropic">Anthropic</option>
                  <option value="openai">OpenAI</option>
                  <option value="lmstudio">LM Studio (local)</option>
                </select>
              </Field>
              <Field label="Model">
                <select className="select" defaultValue={a.model}>
                  <option value="claude-sonnet-4-5">claude-sonnet-4-5</option>
                  <option value="claude-haiku-4-5">claude-haiku-4-5</option>
                  <option value="gpt-5.4">gpt-5.4</option>
                  <option value="gpt-5.4-mini">gpt-5.4-mini</option>
                </select>
              </Field>
              <Field label="Temperature" hint="0.0 for deterministic routing; 0.7 for creative synthesis.">
                <input className="input mono" type="number" step="0.1" defaultValue={0.2}/>
              </Field>
              <Field label="Max output tokens">
                <input className="input mono" type="number" defaultValue={1024}/>
              </Field>
              <Field label="Tool calling">
                <select className="select" defaultValue="auto"><option>auto</option><option>required</option><option>none</option></select>
              </Field>
              <Field label="Seed (optional)" hint="Fix for reproducibility during eval runs.">
                <input className="input mono" placeholder="—"/>
              </Field>
            </div>
          </div>
        </div>
      )}

      {tab === 'skills' && (
        <div className="card">
          <div className="form-section">
            <div className="form-section-head">
              <h3>Skills attached</h3>
              <p>Reusable policy snippets composed into the system prompt at build time.</p>
            </div>
            <div className="stack">
              {['pii-redaction','sql-read-only','safe-ssh'].map(s => (
                <div key={s} style={{display:'flex', alignItems:'center', justifyContent:'space-between', padding:'10px 12px', background:'var(--surface-2)', border:'1px solid var(--border)', borderRadius:'var(--radius)'}}>
                  <div style={{display:'flex',alignItems:'center',gap:10}}>
                    <Ico.skills style={{color:'var(--muted)'}}/>
                    <div>
                      <div className="mono" style={{fontWeight:500}}>{s}</div>
                      <div className="muted small">updated {relTime('2026-04-18T00:00:00Z')}</div>
                    </div>
                  </div>
                  <Button size="sm" variant="ghost" icon={<Ico.x/>}>Remove</Button>
                </div>
              ))}
              <Button size="sm" icon={<Ico.plus/>} style={{alignSelf:'flex-start'}}>Attach skill</Button>
            </div>
          </div>
        </div>
      )}

      {tab === 'outputs' && (
        <div className="card">
          <div className="form-section">
            <div className="form-section-head">
              <h3>Output ports</h3>
              <p>Named exits for workflow routing. Map to <code>decision.port</code> on agent output.</p>
            </div>
            <div className="stack">
              {[
                { name: 'p1', desc: 'Priority 1 — requires compliance HITL' },
                { name: 'p2', desc: 'Priority 2 — standard compose path' },
                { name: 'p3', desc: 'Priority 3 — auto-respond from template' }
              ].map(p => (
                <div key={p.name} style={{display:'grid', gridTemplateColumns:'auto 1fr auto', alignItems:'center', gap:12, padding:'10px 12px', background:'var(--surface-2)', border:'1px solid var(--border)', borderRadius:'var(--radius)'}}>
                  <span className="wf-port filled" style={{'--_kind':'var(--sem-blue)'}}/>
                  <div>
                    <div className="mono" style={{fontWeight:500}}>{p.name}</div>
                    <div className="muted small">{p.desc}</div>
                  </div>
                  <div className="row">
                    <Button size="sm" variant="ghost">Edit</Button>
                    <Button size="sm" variant="ghost" icon={<Ico.x/>}/>
                  </div>
                </div>
              ))}
              <Button size="sm" icon={<Ico.plus/>} style={{alignSelf:'flex-start'}}>Add port</Button>
            </div>
          </div>
        </div>
      )}

      {tab === 'history' && (
        <div className="card" style={{overflow:'hidden'}}>
          <table className="table">
            <thead><tr><th>Version</th><th>Published</th><th>Author</th><th>Runs</th><th>Notes</th><th></th></tr></thead>
            <tbody>
              {[12,11,10,9,8].map(v => (
                <tr key={v}>
                  <td><Chip mono variant={v === 12 ? 'accent' : 'default'}>v{v}</Chip></td>
                  <td className="muted small">{relTime(new Date(Date.now() - (12-v) * 86400000 * 3).toISOString())}</td>
                  <td className="mono small">@{['klee','mtrefry','klee','jchen','mtrefry'][12-v]}</td>
                  <td className="mono">{[842, 1205, 980, 700, 412][12-v]}</td>
                  <td className="muted small">{['Reasoning field cap → 400','Added p1 legal routing','Port rename: urgent → p1','Initial output schema','First publish'][12-v]}</td>
                  <td className="actions"><Button size="sm" variant="ghost">Diff</Button></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

Object.assign(window, { AgentsPage, AgentEditorPage });
