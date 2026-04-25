# Copilot Testing Walkthrough

This document outlines the systematic testing process for verifying the AI Copilot Data Query Engine logic.

## 1. Application Startup
The application is configured to run locally. Start it using `dotnet run`. The application will bind to `http://localhost:5149`.

## 2. Authentication Bypass (Test Mode)
To simplify testing, the application has a testing backdoor:
1. Navigate to the login page: `http://localhost:5149/login` (or the default landing page).
2. Append the parameter `?testing=true` to the URL.
3. This will expose a selector allowing you to directly choose and impersonate an **Admin User**.

## 3. Navigate to Copilot
Once authenticated as an admin, navigate to the **Copilot** module within the dashboard.

## 4. Execute Test Cases
In the Copilot chat, execute the following questions to test the deterministic planner's "clever" operation resolution. Ensure the generated data plan correctly maps the intent without requiring hardcoded lists.

### Easy (Basic Retrieval)
*   **Question:** "Show me all tickets"
*   **Expected:** Operation = `list`, OutputShape = `Table`

### Medium (Filtering and Breakdown)
*   **Question:** "How many tickets by status?"
*   **Expected:** Operation = `breakdown`, OutputShape = `Table` or `Metric`, correctly identifies the grouping by `Status`.

### Hard (Complex Aggregation and Temporal)
*   **Question:** "What is the average resolution time for critical tickets this week?"
*   **Expected:** Operation = `aggregate`, OutputShape = `Metric`, correctly maps the function to `avg`, applies the temporal filter for "this week", and filters priority to "critical".

## 5. Verification
For each test, review the displayed output and the generated data plan to ensure:
* No hard failures on joins.
* Semantic field matching resolves the correct entity/field.
* Proper fallback between the deterministic and model planners.
