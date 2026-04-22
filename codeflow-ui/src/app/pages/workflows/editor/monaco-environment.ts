type MonacoEnvironmentLike = {
  getWorker?: (workerId: string, label: string) => Worker;
};

const editorWorkerFactory = () =>
  new Worker(new URL('./workers/editor.worker.ts', import.meta.url), {
    type: 'module',
    name: 'editorWorkerService'
  });

const typescriptWorkerFactory = () =>
  new Worker(new URL('./workers/ts.worker.ts', import.meta.url), {
    type: 'module',
    name: 'typescript'
  });

let configured = false;

export function ensureMonacoEnvironment(): void {
  if (configured) return;

  const scope = globalThis as typeof globalThis & {
    MonacoEnvironment?: MonacoEnvironmentLike;
  };

  scope.MonacoEnvironment = {
    ...scope.MonacoEnvironment,
    getWorker: (_workerId: string, label: string) => {
      switch (label) {
        case 'javascript':
        case 'typescript':
          return typescriptWorkerFactory();
        default:
          return editorWorkerFactory();
      }
    }
  };

  configured = true;
}
