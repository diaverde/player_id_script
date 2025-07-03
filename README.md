## A few considerations ##
1. Update the appsettings.json file with the corresponding data.
2. The script can be run as it is and it will try to update all records at once but it will take an enormous amount of time. To run in a more controlled way, it can be useful to do it by batches, adjusting the amount of db entries to take adjusting line 110 in Program.cs, which will also allow to check gradually the results.
