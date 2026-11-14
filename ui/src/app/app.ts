import { Component } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink],
  template: `
    <a class="skip-link" href="#main">Skip to content</a>
    <header class="app-header">
      <a routerLink="/claims" class="brand">Claims review</a>
      <span class="muted">Thames Mutual Insurance (fictional)</span>
    </header>
    <main id="main" tabindex="-1"><router-outlet /></main>
  `,
})
export class App {}
