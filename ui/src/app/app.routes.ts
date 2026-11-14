import { Routes } from '@angular/router';
import { ClaimDetailPage } from './claims/claim-detail';
import { ClaimList } from './claims/claim-list';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'claims' },
  { path: 'claims', component: ClaimList, title: 'Claims review' },
  { path: 'claims/:id', component: ClaimDetailPage, title: 'Claim detail - Claims review' },
  { path: '**', redirectTo: 'claims' },
];
