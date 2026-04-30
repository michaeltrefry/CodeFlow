import { Injectable } from '@angular/core';

export interface Logger {
  info(message: string, ...context: unknown[]): void;
  warn(message: string, ...context: unknown[]): void;
  error(message: string, ...context: unknown[]): void;
}

export const consoleLogger: Logger = {
  info(message, ...context) {
    console.info(message, ...context);
  },
  warn(message, ...context) {
    console.warn(message, ...context);
  },
  error(message, ...context) {
    console.error(message, ...context);
  },
};

@Injectable({ providedIn: 'root' })
export class LoggerService implements Logger {
  info(message: string, ...context: unknown[]): void {
    consoleLogger.info(message, ...context);
  }

  warn(message: string, ...context: unknown[]): void {
    consoleLogger.warn(message, ...context);
  }

  error(message: string, ...context: unknown[]): void {
    consoleLogger.error(message, ...context);
  }
}
