import { Component, effect, inject } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';
import { AuthService, DEV_PERSONAS, DevPersona } from './auth/auth.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink],
  template: `
    <a class="skip-link" href="#main">Skip to content</a>
    <header class="app-header">
      <a routerLink="/claims" class="brand">Claims review</a>
      <span class="muted">Thames Mutual Insurance (fictional)</span>
      <span class="spacer"></span>
      @if (auth.mode === 'dev') {
        <div class="dev-banner" role="group" aria-label="Development sign-in">
          <strong>Development sign-in (no Entra ID configured)</strong>
          <label for="persona">Sign in as</label>
          <select id="persona" (change)="pick($any($event.target).value)">
            @for (p of personas; track p.value) {
              <option [value]="p.value" [selected]="p.value === auth.persona()">{{ p.label }}</option>
            }
          </select>
        </div>
      } @else if (auth.signedIn()) {
        <span class="who">{{ auth.me()?.name }}</span>
        <button type="button" (click)="auth.signOut()">Sign out</button>
      }
    </header>
    <main id="main" tabindex="-1">
      @if (auth.signedIn()) {
        <router-outlet />
      } @else {
        <h1>Sign in</h1>
        <p>Sign in with your work account to review claims.</p>
        <button type="button" class="primary" (click)="auth.signIn()">Sign in with Microsoft</button>
      }
    </main>
  `,
})
export class App {
  protected readonly auth = inject(AuthService);
  protected readonly personas = DEV_PERSONAS;

  constructor() {
    effect(() => {
      if (this.auth.signedIn()) void this.auth.loadMe();
    });
  }

  protected pick(value: string): void {
    void this.auth.setPersona(value as DevPersona);
  }
}
