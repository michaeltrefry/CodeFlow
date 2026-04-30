import { bootstrapApplication } from '@angular/platform-browser';
import { AppComponent } from './app/app.component';
import { appConfig } from './app/app.config';
import { loadRuntimeConfig } from './app/core/runtime-config';
import { consoleLogger } from './app/core/logger.service';

// Load runtime-config.json BEFORE Angular bootstraps so the OIDC config built in auth.config.ts
// reflects the env-injected authority/clientId. The same dist bundle then serves any environment.
async function bootstrap(): Promise<void> {
  await loadRuntimeConfig();
  await bootstrapApplication(AppComponent, appConfig);
}

bootstrap().catch(err => consoleLogger.error('[bootstrap] failed:', err));
