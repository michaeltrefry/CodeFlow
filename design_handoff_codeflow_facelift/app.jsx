// app.jsx — root composition

function App() {
  const [tweaks, setTweak] = useTweaks(window.TWEAK_DEFAULTS);
  const [panelOpen, setPanelOpen] = useState(false);
  const [active, setActive] = useState('traces');
  const [trace, setTrace] = useState(null);
  const [workflow, setWorkflow] = useState(null);
  const [agent, setAgent] = useState(null);
  const [agentMode, setAgentMode] = useState(null);

  useEffect(() => {
    const html = document.documentElement;
    html.dataset.theme = tweaks.theme;
    html.dataset.accent = tweaks.accent;
    html.dataset.font = tweaks.fontPair;
  }, [tweaks.theme, tweaks.accent, tweaks.fontPair]);

  useEffect(() => {
    const onMsg = (e) => {
      if (e.data && e.data.type === '__activate_edit_mode') setPanelOpen(true);
      if (e.data && e.data.type === '__deactivate_edit_mode') setPanelOpen(false);
    };
    window.addEventListener('message', onMsg);
    window.parent.postMessage({ type: '__edit_mode_available' }, '*');
    return () => window.removeEventListener('message', onMsg);
  }, []);

  const navigate = (id) => {
    setActive(id);
    setTrace(null); setWorkflow(null); setAgent(null); setAgentMode(null);
  };

  let page;
  let subcrumb = null;

  if (active === 'traces') {
    if (trace) {
      page = <TraceDetailPage trace={trace} onBack={() => setTrace(null)} />;
      subcrumb = trace.traceId.slice(0, 8);
    } else {
      page = <TracesPage onOpenTrace={setTrace} />;
    }
  } else if (active === 'workflows') {
    if (workflow) {
      page = <WorkflowCanvasPage onBack={() => setWorkflow(null)} />;
      subcrumb = `${workflow.key || 'support-triage'} v${workflow.version || 14}`;
    } else {
      page = <WorkflowsListPage onOpenCanvas={setWorkflow} />;
    }
  } else if (active === 'agents') {
    if (agentMode === 'edit' || agentMode === 'new') {
      page = <AgentEditorPage agent={agent} onBack={() => { setAgent(null); setAgentMode(null); }} />;
      subcrumb = agent ? agent.key : 'new';
    } else {
      page = <AgentsPage
        onNewAgent={() => { setAgent(null); setAgentMode('new'); }}
        onEditAgent={(a) => { setAgent(a); setAgentMode('edit'); }} />;
    }
  } else if (active === 'hitl') page = <HitlPage />;
  else if (active === 'dlq')    page = <DlqPage />;
  else if (active === 'mcp')    page = <McpPage />;
  else if (active === 'roles')  page = <RolesPage />;
  else if (active === 'skills') page = <SkillsPage />;
  else if (active === 'git')    page = <GitHostPage />;
  else if (active === 'inventory') page = <InventoryPage />;
  else page = <TracesPage onOpenTrace={setTrace} />;

  return (
    <>
      <Shell
        active={active}
        setActive={navigate}
        collapsed={tweaks.navCollapsed}
        setCollapsed={(v) => setTweak('navCollapsed', v)}
        subcrumb={subcrumb}
        onToggleTweaks={() => setPanelOpen(v => !v)}
      >
        {page}
      </Shell>

      {panelOpen && (
        <TweaksPanel onClose={() => setPanelOpen(false)}>
          <TweakSection label="Theme" />
          <TweakRadio label="Mode" value={tweaks.theme}
            options={['dark','light']}
            onChange={v => setTweak('theme', v)} />
          <TweakRadio label="Accent" value={tweaks.accent}
            options={['indigo','cyan','green','amber']}
            onChange={v => setTweak('accent', v)} />

          <TweakSection label="Typography" />
          <TweakRadio label="Font pair" value={tweaks.fontPair}
            options={['geist','inter','plex']}
            onChange={v => setTweak('fontPair', v)} />

          <TweakSection label="Layout" />
          <TweakToggle label="Collapse nav" value={tweaks.navCollapsed}
            onChange={v => setTweak('navCollapsed', v)} />
        </TweaksPanel>
      )}
    </>
  );
}

const root = ReactDOM.createRoot(document.getElementById('root'));
root.render(<App />);
