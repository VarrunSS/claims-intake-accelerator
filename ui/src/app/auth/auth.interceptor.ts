import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, from, of, switchMap } from 'rxjs';
import { AuthService } from './auth.service';

/**
 * Adds the bearer token to calls to the review API. If no token is available the call goes out without one and
 * the API answers 401, which the pages show as "not signed in".
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.startsWith('/api/')) return next(req);
  const auth = inject(AuthService);
  return from(auth.token()).pipe(
    catchError(() => of(null)),
    switchMap((token) => next(token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req)),
  );
};
