/*
 * The M-source resolver's tests, exposed as a function rather than its own main so the suite stays
 * one binary with one entry point (tests/test_reportlayout.c owns it).
 */

#ifndef PBIX_TEST_MSOURCE_H
#define PBIX_TEST_MSOURCE_H

/* Runs the resolver's tests. Adds the number of checks made to *out_checks when non-NULL, and
 * returns how many of them failed. */
int run_msource_tests(int *out_checks);

#endif /* PBIX_TEST_MSOURCE_H */
