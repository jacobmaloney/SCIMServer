-- Fix GroupMembers table to match the repository expectations
-- This script updates the GroupMembers table to use Value column instead of UserId

-- First, drop the foreign key constraint
ALTER TABLE [dbo].[GroupMembers] DROP CONSTRAINT [FK_GroupMembers_Users];

-- Drop the unique constraint
ALTER TABLE [dbo].[GroupMembers] DROP CONSTRAINT [UQ_GroupMembers_GroupUser];

-- Rename UserId column to Value
EXEC sp_rename 'dbo.GroupMembers.UserId', 'Value', 'COLUMN';

-- Add Type column
ALTER TABLE [dbo].[GroupMembers] ADD [Type] NVARCHAR(50) NULL DEFAULT 'User';

-- Add Primary column
ALTER TABLE [dbo].[GroupMembers] ADD [Primary] BIT NOT NULL DEFAULT 0;

-- Recreate the unique constraint
ALTER TABLE [dbo].[GroupMembers] ADD CONSTRAINT [UQ_GroupMembers_GroupValue] UNIQUE ([GroupId], [Value]);

-- Drop the default constraint on Added column first
DECLARE @constraint_name NVARCHAR(128)
SELECT @constraint_name = name FROM sys.default_constraints 
WHERE parent_object_id = OBJECT_ID('dbo.GroupMembers') 
AND parent_column_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GroupMembers') AND name = 'Added')

IF @constraint_name IS NOT NULL
BEGIN
    EXEC('ALTER TABLE [dbo].[GroupMembers] DROP CONSTRAINT ' + @constraint_name)
END

-- Now drop the Added column
ALTER TABLE [dbo].[GroupMembers] DROP COLUMN [Added];

PRINT 'GroupMembers table updated successfully!';