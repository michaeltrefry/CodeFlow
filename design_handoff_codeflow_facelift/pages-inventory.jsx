// pages-inventory.jsx — Component inventory

function InventoryPage() {
  return (
    <div className="page">
      <PageHeader
        title="Components"
        subtitle="Primitives used across CodeFlow. Reference for visual consistency and a11y checks."
      />

      <div className="stack" style={{gap:16}}>
        <div className="inv-section">
          <div className="inv-title">Buttons</div>
          <div className="inv-row">
            <Button variant="primary" icon={<Ico.plus/>}>Primary</Button>
            <Button>Default</Button>
            <Button variant="ghost">Ghost</Button>
            <Button variant="danger" icon={<Ico.trash/>}>Destructive</Button>
            <Button disabled>Disabled</Button>
          </div>
          <div className="inv-row">
            <Button size="sm" variant="primary">Small primary</Button>
            <Button size="sm">Small</Button>
            <Button size="sm" variant="ghost" icon={<Ico.refresh/>}>Small ghost</Button>
            <Button size="lg" variant="primary">Large primary</Button>
          </div>
        </div>

        <div className="inv-section">
          <div className="inv-title">Chips & status</div>
          <div className="inv-row">
            <Chip>Neutral</Chip>
            <Chip variant="accent">Accent</Chip>
            <Chip variant="accent" dot>With dot</Chip>
            <Chip variant="ok" dot>Completed</Chip>
            <Chip variant="running" dot>Running</Chip>
            <Chip variant="warn" dot>Escalated</Chip>
            <Chip variant="err" dot>Failed</Chip>
            <Chip mono>v14</Chip>
            <Chip mono>agentKey</Chip>
          </div>
        </div>

        <div className="inv-section">
          <div className="inv-title">Form inputs</div>
          <div className="inv-grid">
            <Field label="Text input" hint="Standard single-line field."><input className="input" defaultValue="acme-platform"/></Field>
            <Field label="Monospace / code" hint="Used for keys, IDs, model names."><input className="input mono" defaultValue="support-triage"/></Field>
            <Field label="Select"><select className="select" defaultValue="a"><option value="a">Option A</option><option>Option B</option></select></Field>
            <Field label="Checkbox"><label className="checkbox"><input type="checkbox" defaultChecked/><span>Hide subflow children</span></label></Field>
            <Field label="Textarea" className="span-2"><textarea className="textarea" rows={3} defaultValue={'const region = ctx.tenant.region;\nreturn region.startsWith("us") ? "us" : "eu";'}/></Field>
          </div>
        </div>

        <div className="inv-section">
          <div className="inv-title">Segmented · Tabs</div>
          <div className="inv-row">
            <Segmented value="all" onChange={()=>{}} options={[{value:'all',label:'All'},{value:'r',label:'Running'},{value:'t',label:'Terminal'}]}/>
            <div style={{width:20}}/>
            <Tabs value="prompt" onChange={()=>{}} items={[
              {value:'identity',label:'Identity'},
              {value:'prompt',label:'Prompt'},
              {value:'model',label:'Model', count: 2},
              {value:'history',label:'Versions'}
            ]}/>
          </div>
        </div>

        <div className="inv-section">
          <div className="inv-title">Toasts</div>
          <div className="inv-row" style={{gap:12}}>
            <div className="toast ok">
              <span className="toast-ico" style={{color:'var(--sem-green)'}}><Ico.check/></span>
              <div className="toast-body"><div className="toast-title">Workflow v15 published</div><div className="toast-desc">New runs will use v15 within 30s.</div></div>
            </div>
            <div className="toast info">
              <span className="toast-ico" style={{color:'var(--accent)'}}><Ico.bell/></span>
              <div className="toast-body"><div className="toast-title">3 HITL tasks awaiting review</div><div className="toast-desc">Oldest has been waiting 14m.</div></div>
            </div>
            <div className="toast err">
              <span className="toast-ico" style={{color:'var(--sem-red)'}}><Ico.alert/></span>
              <div className="toast-body"><div className="toast-title">DLQ retry failed</div><div className="toast-desc">Provider returned 429. Try again in 60s.</div></div>
            </div>
          </div>
        </div>

        <div className="inv-section">
          <div className="inv-title">Empty state</div>
          <Empty icon={<Ico.traces/>} title="No traces yet"
            desc="Workflows will show up here as soon as someone runs one."
            action={<Button variant="primary" icon={<Ico.plus/>}>Submit run</Button>}/>
        </div>

        <div className="inv-section">
          <div className="inv-title">Color · semantic</div>
          <div className="inv-grid">
            {[
              ['--accent','accent'],
              ['--sem-green','ok'],
              ['--sem-amber','warn'],
              ['--sem-red','err'],
              ['--sem-blue','running'],
              ['--sem-purple','hitl']
            ].map(([v,name]) => (
              <div key={v}>
                <div className="swatch" style={{background:`var(${v})`}}>{v}</div>
                <div className="muted small mono" style={{marginTop:6}}>{name}</div>
              </div>
            ))}
          </div>
        </div>

        <div className="inv-section">
          <div className="inv-title">Color · surfaces</div>
          <div className="inv-grid">
            {[
              ['--bg','page background'],
              ['--surface','surface'],
              ['--surface-2','surface-2'],
              ['--surface-3','surface-3'],
              ['--border','border'],
              ['--text-2','text-2']
            ].map(([v,name]) => (
              <div key={v}>
                <div className="swatch" style={{background:`var(${v})`, color:'var(--text)', textShadow:'none'}}>{v}</div>
                <div className="muted small mono" style={{marginTop:6}}>{name}</div>
              </div>
            ))}
          </div>
        </div>

        <div className="inv-section">
          <div className="inv-title">Type scale</div>
          <div className="stack" style={{gap:10}}>
            <div><span className="muted small mono" style={{marginRight:12}}>h1 · 28px</span><span style={{fontSize:'var(--fs-h1)', fontWeight:600, letterSpacing:'-0.02em'}}>Traces that tell the story</span></div>
            <div><span className="muted small mono" style={{marginRight:12}}>h2 · 22px</span><span style={{fontSize:'var(--fs-h2)', fontWeight:600}}>Workflow v14 is active</span></div>
            <div><span className="muted small mono" style={{marginRight:12}}>h3 · 18px</span><span style={{fontSize:'var(--fs-h3)', fontWeight:600}}>Section heading</span></div>
            <div><span className="muted small mono" style={{marginRight:12}}>body · 13px</span>The quick brown agent routes the ticket to the compliance HITL.</div>
            <div><span className="muted small mono" style={{marginRight:12}}>mono · 12px</span><span className="mono">const traceId = "7a2f91e4-4a88-4c3e-92df-d4a6b19e2134";</span></div>
          </div>
        </div>

        <div className="inv-section">
          <div className="inv-title">Keyboard affordances</div>
          <div className="inv-row">
            <span className="row" style={{gap:4}}><span className="kbd">⌘</span><span className="kbd">K</span><span className="muted small" style={{marginLeft:6}}>Search</span></span>
            <span className="row" style={{gap:4}}><span className="kbd">J</span><span className="kbd">K</span><span className="muted small" style={{marginLeft:6}}>Next / prev row</span></span>
            <span className="row" style={{gap:4}}><span className="kbd">E</span><span className="muted small" style={{marginLeft:6}}>Edit selected</span></span>
            <span className="row" style={{gap:4}}><span className="kbd">⇧</span><span className="kbd">R</span><span className="muted small" style={{marginLeft:6}}>Retry DLQ</span></span>
            <span className="row" style={{gap:4}}><span className="kbd">?</span><span className="muted small" style={{marginLeft:6}}>Keyboard help</span></span>
          </div>
        </div>

        <div className="inv-section">
          <div className="inv-title">Accessibility notes</div>
          <ul style={{margin:0, paddingLeft:18, color:'var(--text-2)', fontSize:'var(--fs-md)', lineHeight:1.75}}>
            <li>Body text hits AA (7:1 in dark, 13:1 in light) against the page background.</li>
            <li>Status is never color-only — every chip pairs a dot with a label; failure rows also carry a destructive icon in the row actions.</li>
            <li>Focus rings use a 2px halo in accent color with a 2px page-bg gap for legibility on any surface.</li>
            <li>Workflow node kinds encode state twice: left-border color <em>and</em> an uppercase text kind chip.</li>
            <li>All primary actions are reachable via tab; the top-bar Search is anchored to ⌘K.</li>
          </ul>
        </div>
      </div>
    </div>
  );
}

Object.assign(window, { InventoryPage });
